[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$HistoryVulcanPackageRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path $repoRoot 'z-Publish'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = $publishRoot
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
# Diana 的发布器把候选先写到临时 OutputRoot，再提升为 z-Publish/HistoryMercury-vX.Y.Z。
# 省略参数时仍默认项目内 z-Publish；显式传入时允许库外路径。

$projectPath = Join-Path $repoRoot 'b-Code-MercuryDock\HistoryMercury.csproj'
$manifestSource = Join-Path $repoRoot 'b-Code-MercuryDock\module.manifest.json'
$packageDocuments = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'b-Office\package') -Filter '*.md' -File)
if ($packageDocuments.Count -eq 0) {
    throw 'b-Office/package must contain at least one Markdown document'
}
$releaseRoot = Join-Path $repoRoot "b-Code-MercuryDock\bin\$Configuration\net8.0-windows"
$transactionId = [Guid]::NewGuid().ToString('N')
$transactionRoot = Join-Path ([IO.Path]::GetTempPath()) "HistoryMercury.Package.$transactionId"
$stage = Join-Path $transactionRoot 'candidate'
$backup = Join-Path $transactionRoot 'previous'

function Assert-ModulePackage {
    param([string]$Root, [string]$ExpectedVersion)

    $expectedFiles = @('HistoryMercury.dll', 'HistoryMercury.xml', 'module.manifest.json', 'SHA256SUMS') +
        @($packageDocuments | ForEach-Object { "docs/$($_.Name)" }) | Sort-Object
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $historyPrefix = $rootPrefix + 'history\'
    $actualFiles = @(Get-ChildItem -LiteralPath $Root -File -Recurse |
        Where-Object { -not $_.FullName.StartsWith($historyPrefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
        $_.FullName.Substring($rootPrefix.Length).Replace('\', '/')
    } | Sort-Object)
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
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<file>.+)$') {
            throw "Invalid HistoryMercury checksum line: $line"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }
    $hashTargets = @($expectedFiles | Where-Object { $_ -ne 'SHA256SUMS' })
    foreach ($file in $hashTargets) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $Root $file.Replace('/', '\')) -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($file) -or $hashes[$file] -ne $actual) {
            throw "HistoryMercury checksum mismatch: $file"
        }
    }
    if ($hashes.Count -ne $hashTargets.Count) {
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

New-Item -ItemType Directory -Force -Path $transactionRoot, $backup, $OutputRoot | Out-Null
$buildProperties = @('-p:NuGetAudit=false')
if (-not [string]::IsNullOrWhiteSpace($HistoryVulcanPackageRoot)) {
    $buildProperties += "-p:HistoryVulcanPackageRoot=$HistoryVulcanPackageRoot"
}
# SDK 9 WPF 会把上一轮 obj 里的 AssemblyInfo 再编进主工程；发布前清掉中间产物。
$projectRoot = Split-Path -Parent $projectPath
foreach ($stale in @((Join-Path $projectRoot 'obj'), (Join-Path $projectRoot 'bin'))) {
    if (Test-Path -LiteralPath $stale) {
        Remove-Item -LiteralPath $stale -Recurse -Force
    }
}
& dotnet build $projectPath -c $Configuration @buildProperties
if ($LASTEXITCODE -ne 0) {
    throw "HistoryMercury $Configuration build failed with exit code $LASTEXITCODE"
}

try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryMercury.dll') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryMercury.xml') -Destination $stage
    Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $stage 'module.manifest.json')
    $docsRoot = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Force -Path $docsRoot | Out-Null
    foreach ($document in $packageDocuments) {
        Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $docsRoot $document.Name)
    }
    $relativeFiles = @('HistoryMercury.dll', 'HistoryMercury.xml', 'module.manifest.json') +
        @($packageDocuments | ForEach-Object { "docs/$($_.Name)" })
    $checksumLines = foreach ($file in $relativeFiles) {
        "$((Get-FileHash -LiteralPath (Join-Path $stage $file.Replace('/', '\')) -Algorithm SHA256).Hash)  $file"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $stage 'SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))
    Assert-ModulePackage $stage $version

    $movedPrevious = [Collections.Generic.List[string]]::new()
    $movedCandidate = [Collections.Generic.List[string]]::new()
    try {
        foreach ($item in @(Get-ChildItem -LiteralPath $OutputRoot -Force |
                Where-Object { $_.Name -ne 'history' })) {
            Move-Item -LiteralPath $item.FullName -Destination $backup
            $movedPrevious.Add($item.Name)
        }
        foreach ($item in @(Get-ChildItem -LiteralPath $stage -Force)) {
            Move-Item -LiteralPath $item.FullName -Destination $OutputRoot
            $movedCandidate.Add($item.Name)
        }
        Assert-ModulePackage $OutputRoot $version
    }
    catch {
        foreach ($name in $movedCandidate) {
            $path = Join-Path $OutputRoot $name
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
        foreach ($name in $movedPrevious) {
            $path = Join-Path $backup $name
            if (Test-Path -LiteralPath $path) {
                Move-Item -LiteralPath $path -Destination $OutputRoot
            }
        }
        throw
    }
    Write-Host "HistoryMercury $version candidate package created: $OutputRoot"
}
finally {
    if (Test-Path -LiteralPath $transactionRoot) {
        Remove-Item -LiteralPath $transactionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
