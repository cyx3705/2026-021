[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $repoRoot 'b-Publish'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $publishRoot 'current\HistoryMercury'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside the HistoryMercury project: $OutputRoot"
}

$projectPath = Join-Path $repoRoot 'b-Code-MercuryDock\HistoryMercury.csproj'
$manifestSource = Join-Path $repoRoot 'b-Code-MercuryDock\module.manifest.json'
$releaseRoot = Join-Path $repoRoot "b-Code-MercuryDock\bin\$Configuration\net8.0-windows"
$workRoot = Join-Path $publishRoot 'work'
$transactionId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $workRoot "HistoryMercury-candidate-$transactionId"
$backup = Join-Path $workRoot "HistoryMercury-previous-$transactionId"
$failed = Join-Path $workRoot "HistoryMercury-failed-$transactionId"

function Assert-ModulePackage {
    param([string]$Root, [string]$ExpectedVersion)

    $expectedFiles = @('HistoryMercury.dll', 'HistoryMercury.xml', 'module.manifest.json', 'SHA256SUMS') | Sort-Object
    $actualFiles = @(Get-ChildItem -LiteralPath $Root -File | Select-Object -ExpandProperty Name | Sort-Object)
    if (($actualFiles -join "`n") -ne ($expectedFiles -join "`n")) {
        throw "HistoryMercury package file set is invalid: $($actualFiles -join ', ')"
    }
    $manifest = [IO.File]::ReadAllText((Join-Path $Root 'module.manifest.json'), [Text.UTF8Encoding]::new($false)) |
        ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.type -ne 'HistoryVulcan.Module' -or
        $manifest.name -ne 'HistoryMercury' -or $manifest.version -ne $ExpectedVersion -or
        $manifest.artifact -ne 'HistoryMercury.dll' -or $manifest.docs -ne 'HistoryMercury.xml') {
        throw "HistoryMercury manifest identity does not match $ExpectedVersion"
    }
    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $Root 'SHA256SUMS'), [Text.UTF8Encoding]::new($false))) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<file>[^\\/]+)$') {
            throw "Invalid HistoryMercury checksum line: $line"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }
    foreach ($file in @('HistoryMercury.dll', 'HistoryMercury.xml', 'module.manifest.json')) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $Root $file) -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($file) -or $hashes[$file] -ne $actual) {
            throw "HistoryMercury checksum mismatch: $file"
        }
    }
    if ($hashes.Count -ne 3) {
        throw 'HistoryMercury checksum does not cover the complete package'
    }
}

$projectXml = [xml][IO.File]::ReadAllText($projectPath, [Text.UTF8Encoding]::new($false))
$versionValues = @($projectXml.Project.PropertyGroup | ForEach-Object {
    if ($_.PSObject.Properties.Name -contains 'Version') {
        $_.PSObject.Properties['Version'].Value
    }
} | Where-Object { $_ })
if ($versionValues.Count -ne 1) {
    throw 'HistoryMercury.csproj must declare exactly one Version value'
}
$version = [string]$versionValues[0]
$sourceManifest = [IO.File]::ReadAllText($manifestSource, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
if ($version -notmatch '^\d+\.\d+\.\d+$' -or
    $sourceManifest.name -ne 'HistoryMercury' -or $sourceManifest.version -ne $version) {
    throw 'HistoryMercury project version and source manifest are not aligned'
}

New-Item -ItemType Directory -Force -Path $workRoot, (Split-Path -Parent $OutputRoot) | Out-Null
& dotnet build $projectPath -c $Configuration -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) {
    throw "HistoryMercury $Configuration build failed with exit code $LASTEXITCODE"
}

$promoted = $false
$backedUp = $false
try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryMercury.dll') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryMercury.xml') -Destination $stage
    Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $stage 'module.manifest.json')
    $checksumLines = foreach ($file in @('HistoryMercury.dll', 'HistoryMercury.xml', 'module.manifest.json')) {
        "$((Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash)  $file"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $stage 'SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))
    Assert-ModulePackage $stage $version

    try {
        if (Test-Path -LiteralPath $OutputRoot) {
            Move-Item -LiteralPath $OutputRoot -Destination $backup
            $backedUp = $true
        }
        Move-Item -LiteralPath $stage -Destination $OutputRoot
        $promoted = $true
        Assert-ModulePackage $OutputRoot $version
        if ($backedUp) {
            Remove-Item -LiteralPath $backup -Recurse -Force
            $backedUp = $false
        }
    }
    catch {
        if ($promoted -and (Test-Path -LiteralPath $OutputRoot)) {
            Move-Item -LiteralPath $OutputRoot -Destination $failed
        }
        if ($backedUp -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $OutputRoot
        }
        throw
    }
    Write-Host "HistoryMercury $version candidate package created: $OutputRoot"
}
finally {
    foreach ($path in @($stage, $backup, $failed)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
