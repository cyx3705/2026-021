[CmdletBinding()]
param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $repoRoot 'b-Publish'
$candidateRoot = Join-Path $publishRoot 'current'
$historyRoot = Join-Path $publishRoot 'history'
$workRoot = Join-Path $publishRoot 'work'
$transactionId = [Guid]::NewGuid().ToString('N')
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')

$modules = @(
    [ordered]@{
        Name = 'HistoryMercury'
        Project = 'b-Code-MercuryDock\\HistoryMercury.csproj'
        Manifest = 'b-Code-MercuryDock\\module.manifest.json'
        Release = 'b-Code-MercuryDock\\bin\\Release\\net8.0-windows'
        ZDirectory = 'z-HistoryMercury'
        PreviousNames = @('MercuryDock')
        PreviousZDirectories = @('z-MercuryDock')
    }
)

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Assert-ModuleTree {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $manifestPath = Join-Path $Root 'module.manifest.json'
    $sumsPath = Join-Path $Root 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
        throw "Module snapshot is incomplete: $Root"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.type -ne 'HistoryVulcan.Module' -or
        $manifest.name -ne $Name -or $manifest.version -ne $Version -or
        $manifest.artifact -ne "$Name.dll" -or $manifest.docs -ne "$Name.xml") {
        throw "Module manifest does not match $Name ${Version}: $manifestPath"
    }
    if ([IO.Path]::IsPathRooted([string]$manifest.artifact) -or [IO.Path]::IsPathRooted([string]$manifest.docs) -or
        $manifest.artifact.Contains('..') -or $manifest.docs.Contains('..')) {
        throw "Module manifest contains an escaping path: $manifestPath"
    }

    $expected = @("$Name.dll", "$Name.xml", 'module.manifest.json', 'SHA256SUMS') | Sort-Object
    $actual = Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $_.FullName.Substring($Root.TrimEnd('\\').Length + 1).Replace('\\', '/')
    } | Sort-Object
    if (($actual -join "`n") -ne ($expected -join "`n")) {
        throw "Module snapshot has an unexpected file set: $Root"
    }

    $hashes = @{}
    foreach ($line in Get-Content -LiteralPath $sumsPath -Encoding UTF8) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<file>[^\\/]+)$') {
            throw "Invalid SHA256SUMS line in ${sumsPath}: $line"
        }
        if ($hashes.ContainsKey($Matches.file)) {
            throw "Duplicate SHA256SUMS file in ${sumsPath}: $($Matches.file)"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }
    $hashedFiles = @("$Name.dll", "$Name.xml", 'module.manifest.json') | Sort-Object
    if (($hashes.Keys | Sort-Object) -join "`n" -ne ($hashedFiles -join "`n")) {
        throw "SHA256SUMS file set does not match module snapshot: $sumsPath"
    }
    foreach ($file in $hashedFiles) {
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $Root $file) -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($hashes[$file] -ne $actualHash) {
            throw "SHA256 mismatch for $file in $Root"
        }
    }
    if (@(Get-ChildItem -LiteralPath $Root -Filter 'HistoryVulcan*.dll' -File).Count -ne 0) {
        throw "Module snapshot must not copy HistoryVulcan assemblies: $Root"
    }
}

function New-ModuleTree {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Module,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $sourceManifest = Join-Path $repoRoot $Module.Manifest
    $releaseRoot = Join-Path $repoRoot $Module.Release
    $version = [string]((Get-Content -LiteralPath $sourceManifest -Raw -Encoding UTF8 | ConvertFrom-Json).version)
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $releaseRoot "$($Module.Name).dll") -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $releaseRoot "$($Module.Name).xml") -Destination $Destination
    Copy-Item -LiteralPath $sourceManifest -Destination $Destination

    $hashLines = foreach ($file in @("$($Module.Name).dll", "$($Module.Name).xml", 'module.manifest.json') | Sort-Object) {
        "$(Get-FileHash -LiteralPath (Join-Path $Destination $file) -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $file"
    }
    [IO.File]::WriteAllLines((Join-Path $Destination 'SHA256SUMS'), $hashLines, [Text.UTF8Encoding]::new($false))
    Assert-ModuleTree $Destination $Module.Name $version
    return $version
}

function Invoke-Promotion {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][scriptblock]$Validate
    )

    $promoted = $false
    try {
        & $Validate $Candidate
        if (Test-Path -LiteralPath $Destination) {
            Move-Item -LiteralPath $Destination -Destination $Backup
        }
        Move-Item -LiteralPath $Candidate -Destination $Destination
        $promoted = $true
        & $Validate $Destination
    }
    catch {
        if ($promoted -and (Test-Path -LiteralPath $Destination)) {
            Move-Item -LiteralPath $Destination -Destination "$Destination.failed-$transactionId"
        }
        if (Test-Path -LiteralPath $Backup) {
            Move-Item -LiteralPath $Backup -Destination $Destination
        }
        throw
    }
}

$sourceStatus = (& git -C $repoRoot status --porcelain -- ':!b-Publish/**') -join "`n"
if ($Publish -and -not [string]::IsNullOrWhiteSpace($sourceStatus)) {
    throw "Formal publish requires committed source and documentation:`n$sourceStatus"
}

New-Item -ItemType Directory -Force -Path $candidateRoot, $historyRoot, $workRoot | Out-Null
try {
    foreach ($module in $modules) {
        Invoke-Dotnet @('build', (Join-Path $repoRoot $module.Project), '-c', 'Release', '-p:NuGetAudit=false')
        $stage = Join-Path $workRoot "$($module.Name)-candidate-$transactionId"
        $version = New-ModuleTree $module $stage
        $candidate = Join-Path $candidateRoot $module.Name
        $candidateBackup = Join-Path $workRoot "$($module.Name)-candidate-previous-$transactionId"
        $validate = { param($root) Assert-ModuleTree $root $module.Name $version }
        Invoke-Promotion $stage $candidate $candidateBackup $validate
        if (Test-Path -LiteralPath $candidateBackup) { Remove-Item -LiteralPath $candidateBackup -Recurse -Force }
        foreach ($previousName in @($module.PreviousNames)) {
            $obsoleteCandidate = Join-Path $candidateRoot $previousName
            if (Test-Path -LiteralPath $obsoleteCandidate) {
                Remove-Item -LiteralPath $obsoleteCandidate -Recurse -Force
            }
        }
        Write-Host "Candidate $($module.Name) ${version}: $candidate"
    }

    if ($Publish) {
        foreach ($module in $modules) {
            $candidate = Join-Path $candidateRoot $module.Name
            $manifest = Get-Content -LiteralPath (Join-Path $candidate 'module.manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $version = [string]$manifest.version
            $formalStage = Join-Path $workRoot "$($module.Name)-formal-$transactionId"
            Copy-Item -LiteralPath $candidate -Destination $formalStage -Recurse
            $destination = Join-Path $repoRoot $module.ZDirectory
            $backup = Join-Path $historyRoot "$($module.Name)\\$version-$stamp"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
            $validate = { param($root) Assert-ModuleTree $root $module.Name $version }
            $retiredSlots = @()
            try {
                foreach ($previousZDirectory in @($module.PreviousZDirectories)) {
                    $previousSlot = Join-Path $repoRoot $previousZDirectory
                    if (-not (Test-Path -LiteralPath $previousSlot)) {
                        continue
                    }
                    $previousName = Split-Path -Leaf $previousZDirectory
                    $retired = Join-Path $historyRoot "$previousName\\retired-$stamp"
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $retired) | Out-Null
                    Move-Item -LiteralPath $previousSlot -Destination $retired
                    $retiredSlots += [pscustomobject]@{ Slot = $previousSlot; Archive = $retired }
                }
                Invoke-Promotion $formalStage $destination $backup $validate
            }
            catch {
                foreach ($retired in $retiredSlots) {
                    if ((Test-Path -LiteralPath $retired.Archive) -and -not (Test-Path -LiteralPath $retired.Slot)) {
                        Move-Item -LiteralPath $retired.Archive -Destination $retired.Slot
                    }
                }
                throw
            }
            Write-Host "Formal $($module.Name) ${version}: $destination"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
