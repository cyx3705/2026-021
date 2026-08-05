[CmdletBinding()]
param(
    [switch]$Instantiation
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$errors = [System.Collections.Generic.List[string]]::new()

function Add-ContractError {
    param([string]$Message)

    $script:errors.Add($Message)
}

function Resolve-ContractPath {
    param(
        [string]$RelativePath,
        [string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        Add-ContractError "$Context cannot be empty."
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        Add-ContractError "$Context must be a repository-relative path: $RelativePath"
        return $null
    }

    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $RelativePath))
    $rootPrefix = $projectRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($fullPath -ne $projectRoot -and -not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-ContractError "$Context points outside the repository: $RelativePath"
        return $null
    }

    return $fullPath
}

function Test-RequiredProperty {
    param(
        [object]$Object,
        [string]$Name,
        [string]$Context
    )

    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        Add-ContractError "$Context is missing property '$Name'."
        return $false
    }

    return $true
}

$manifestPath = Join-Path $projectRoot 'project.manifest.json'
$manifest = $null

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Add-ContractError 'Missing project.manifest.json.'
}
else {
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Add-ContractError "project.manifest.json is not valid JSON: $($_.Exception.Message)"
    }
}

$requiredFiles = @(
    'AGENTS.md',
    'README.md',
    '.ignore',
    'b-Code/README.md',
    'b-Code/Test-ProjectContract.ps1'
)

foreach ($relativePath in $requiredFiles) {
    $fullPath = Resolve-ContractPath -RelativePath $relativePath -Context 'Required file path'
    if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Add-ContractError "Missing required file: $relativePath"
    }
}

if ($null -ne $manifest) {
    if ((Test-RequiredProperty $manifest 'schemaVersion' 'manifest') -and $manifest.schemaVersion -ne 1) {
        Add-ContractError "Unsupported schemaVersion: $($manifest.schemaVersion)"
    }

    $null = Test-RequiredProperty $manifest 'template' 'manifest'
    $null = Test-RequiredProperty $manifest 'project' 'manifest'
    $null = Test-RequiredProperty $manifest 'paths' 'manifest'
    $null = Test-RequiredProperty $manifest 'documents' 'manifest'
    $null = Test-RequiredProperty $manifest 'commands' 'manifest'
    $null = Test-RequiredProperty $manifest 'contextExclusions' 'manifest'

    if ($null -ne $manifest.project) {
        foreach ($name in @('id', 'name', 'title', 'kind', 'status')) {
            if (Test-RequiredProperty $manifest.project $name 'project') {
                $value = $manifest.project.$name
                if ($null -eq $value -or @($value).Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]@($value)[0])) {
                    Add-ContractError "project.$name cannot be empty."
                }
            }
        }
    }

    if ($null -ne $manifest.template -and (Test-RequiredProperty $manifest.template 'isTemplate' 'template')) {
        if ($manifest.template.isTemplate -isnot [bool]) {
            Add-ContractError 'template.isTemplate must be a Boolean.'
        }
        elseif ($Instantiation -and $manifest.template.isTemplate) {
            Add-ContractError 'Instantiation validation requires template.isTemplate to be false.'
        }
    }

    if ($null -ne $manifest.paths) {
        foreach ($name in @('activeRoots', 'sourceRoots', 'generatedRoots', 'archiveRoots', 'allowedRootDirectoryPrefixes')) {
            $null = Test-RequiredProperty $manifest.paths $name 'paths'
        }

        foreach ($relativePath in @($manifest.paths.activeRoots)) {
            $fullPath = Resolve-ContractPath -RelativePath $relativePath -Context 'Active root'
            if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
                Add-ContractError "Declared active root does not exist: $relativePath"
            }
        }

        foreach ($relativePath in @($manifest.paths.sourceRoots)) {
            $fullPath = Resolve-ContractPath -RelativePath $relativePath -Context 'Source root'
            if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
                Add-ContractError "Declared source root does not exist: $relativePath"
            }
        }

        $allowedPrefixes = @($manifest.paths.allowedRootDirectoryPrefixes)
        foreach ($expectedPrefix in @('a-', 'b-', 'z-')) {
            if ($allowedPrefixes -notcontains $expectedPrefix) {
                Add-ContractError "paths.allowedRootDirectoryPrefixes must include '$expectedPrefix'."
            }
        }

        foreach ($prefix in $allowedPrefixes) {
            if ([string]::IsNullOrWhiteSpace([string]$prefix)) {
                Add-ContractError 'Root directory prefixes cannot be empty.'
            }
        }

        foreach ($directory in Get-ChildItem -LiteralPath $projectRoot -Directory -Force) {
            if ($directory.Name.StartsWith('.')) {
                continue
            }

            $isAllowed = $false
            foreach ($prefix in $allowedPrefixes) {
                if ($directory.Name.StartsWith([string]$prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $isAllowed = $true
                    break
                }
            }

            if (-not $isAllowed) {
                Add-ContractError "Root directory must use an a-, b-, or z- prefix: $($directory.Name)"
            }
        }
    }

    if ($null -ne $manifest.documents) {
        foreach ($property in $manifest.documents.PSObject.Properties) {
            $fullPath = Resolve-ContractPath -RelativePath ([string]$property.Value) -Context "documents.$($property.Name)"
            if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
                Add-ContractError "Declared document does not exist: $($property.Value)"
            }
        }
    }

    if ($null -ne $manifest.commands) {
        foreach ($name in @('setup', 'build', 'test', 'verify', 'run', 'package')) {
            if (Test-RequiredProperty $manifest.commands $name 'commands') {
                $value = $manifest.commands.$name
                if ($null -ne $value -and ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value))) {
                    Add-ContractError "commands.$name must be a non-empty string or null."
                }
            }
        }
    }
}

$markdownFiles = Get-ChildItem -LiteralPath $projectRoot -Filter '*.md' -File -Recurse |
    Where-Object {
        $_.FullName -notlike (Join-Path $projectRoot 'b-Office\history\*')
    }

$linkPattern = [regex]'\[[^\]]+\]\((?<target>[^)]+)\)'
foreach ($file in $markdownFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($match in $linkPattern.Matches($content)) {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if ($target.StartsWith('#') -or $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
            continue
        }

        $pathPart = ($target -split '#', 2)[0]
        if ([string]::IsNullOrWhiteSpace($pathPart)) {
            continue
        }

        try {
            $pathPart = [System.Uri]::UnescapeDataString($pathPart)
        }
        catch {
            Add-ContractError "Markdown link cannot be decoded: $($file.FullName) -> $target"
            continue
        }

        $linkedPath = [System.IO.Path]::GetFullPath((Join-Path $file.DirectoryName $pathPart))
        if (-not (Test-Path -LiteralPath $linkedPath)) {
            $relativeFile = $file.FullName.Substring($projectRoot.Length + 1)
            Add-ContractError "Broken Markdown link: $relativeFile -> $target"
        }
    }
}

$isTemplate = $true
if ($null -ne $manifest -and $null -ne $manifest.template -and $manifest.template.isTemplate -is [bool]) {
    $isTemplate = $manifest.template.isTemplate
}

$strictContentMode = $Instantiation -or -not $isTemplate
if ($strictContentMode) {
    if ($null -ne $manifest -and $null -ne $manifest.project) {
        if ($manifest.project.id -eq '0000-001') {
            Add-ContractError 'Instantiated projects must replace the template project.id.'
        }
        if ($manifest.project.name -eq 'AIReady') {
            Add-ContractError 'Instantiated projects must replace the template project.name.'
        }
        if ($manifest.project.status -eq 'template') {
            Add-ContractError 'Instantiated projects must replace the template project.status.'
        }
    }

    $rootReadmePath = Join-Path $projectRoot 'README.md'
    if (Test-Path -LiteralPath $rootReadmePath -PathType Leaf) {
        $rootReadme = Get-Content -LiteralPath $rootReadmePath -Raw -Encoding UTF8
        if ($rootReadme -match '(?m)^# OneHistory AI-Ready') {
            Add-ContractError 'Instantiated projects must replace the template root README.'
        }
    }

    $placeholderPattern = [regex]'\{\{[^{}\r\n]+\}\}'
    $currentDocumentPaths = @()
    if ($null -ne $manifest -and $null -ne $manifest.documents) {
        $currentDocumentPaths = @(
            $manifest.documents.PSObject.Properties.Value |
                Where-Object { [string]$_ -like 'b-Office/current/*' }
        )
    }

    $filesToCheck = @('README.md', 'project.manifest.json') + $currentDocumentPaths

    foreach ($relativePath in $filesToCheck | Select-Object -Unique) {
        $fullPath = Join-Path $projectRoot $relativePath
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
            if ($placeholderPattern.IsMatch($content)) {
                Add-ContractError "Instantiation placeholder remains in: $relativePath"
            }
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host "Project contract validation failed with $($errors.Count) error(s):" -ForegroundColor Red
    foreach ($contractError in $errors) {
        Write-Host "  - $contractError" -ForegroundColor Red
    }
    exit 1
}

$mode = if ($Instantiation) { 'instantiation' } else { 'template/project' }
Write-Host "Project contract validation passed ($mode mode)." -ForegroundColor Green
