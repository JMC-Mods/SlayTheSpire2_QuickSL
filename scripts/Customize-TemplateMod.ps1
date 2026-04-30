[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$ModId,

    [string]$DisplayName = $ModId,
    [string]$Author,
    [string]$Description,

    [string]$OldModId = 'TemplateMod',
    [string]$OldGodotProjectName = 'TemplateMod',
    [string[]]$OldTemplateNames = @('TemplateMod'),

    [string[]]$Replace = @()
)

$ErrorActionPreference = 'Stop'

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$ScriptPath = $PSCommandPath
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$SkipDirectories = @('.git', '.vs', 'bin', 'obj')
$SkipContentExtensions = @(
    '.csproj',
    '.dll',
    '.exe',
    '.pdb',
    '.png',
    '.jpg',
    '.jpeg',
    '.webp',
    '.ico',
    '.pck'
)

$TextExtensions = @(
    '.cs',
    '.json',
    '.sln',
    '.slnx',
    '.md',
    '.txt',
    '.cfg',
    '.godot',
    '.import',
    '.yml',
    '.yaml',
    '.props',
    '.targets',
    '.editorconfig',
    '.gitignore',
    '.gitattributes'
)

$Replacements = New-Object System.Collections.Generic.List[object]

function Add-Replacement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$From,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$To
    )

    if ([string]::IsNullOrEmpty($From)) {
        throw 'Replacement source cannot be empty.'
    }

    foreach ($Replacement in $Replacements) {
        if ($Replacement.From -ceq $From) {
            return
        }
    }

    $Replacements.Add([pscustomobject]@{
        From = $From
        To = $To
    }) | Out-Null
}

Add-Replacement -From $OldModId -To $ModId

$OldModIdLower = $OldModId.ToLowerInvariant()
if ($OldModIdLower -ne $OldModId) {
    Add-Replacement -From $OldModIdLower -To $ModId.ToLowerInvariant()
}

if (-not [string]::IsNullOrWhiteSpace($OldGodotProjectName)) {
    Add-Replacement -From $OldGodotProjectName -To $ModId
}

foreach ($OldTemplateName in $OldTemplateNames) {
    if ([string]::IsNullOrWhiteSpace($OldTemplateName)) {
        continue
    }

    Add-Replacement -From $OldTemplateName -To $ModId

    $OldTemplateNameLower = $OldTemplateName.ToLowerInvariant()
    if ($OldTemplateNameLower -ne $OldTemplateName) {
        Add-Replacement -From $OldTemplateNameLower -To $ModId.ToLowerInvariant()
    }
}

foreach ($Item in $Replace) {
    $SeparatorIndex = $Item.IndexOf('=')
    if ($SeparatorIndex -le 0) {
        throw "Invalid replacement '$Item'. Use the form old=new."
    }

    $From = $Item.Substring(0, $SeparatorIndex)
    $To = $Item.Substring($SeparatorIndex + 1)
    Add-Replacement -From $From -To $To
}

function Get-RelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $FullPath = (Resolve-Path -LiteralPath $Path).Path
    return $FullPath.Substring($Root.Length).TrimStart('\', '/')
}

function Test-SkippedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $RelativePath = Get-RelativePath -Path $Path
    $Segments = $RelativePath -split '[\\/]'
    foreach ($Segment in $Segments) {
        if ($SkipDirectories -contains $Segment) {
            return $true
        }
    }

    return $false
}

function Test-SkippedTextFile {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$File)

    if ($File.FullName -eq $ScriptPath) {
        return $true
    }

    if (Test-SkippedPath -Path $File.FullName) {
        return $true
    }

    $Extension = $File.Extension.ToLowerInvariant()
    if ($SkipContentExtensions -contains $Extension) {
        return $true
    }

    if ($TextExtensions -notcontains $Extension) {
        return $true
    }

    return $false
}

function Test-SkippedRenameFile {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$File)

    if ($File.FullName -eq $ScriptPath) {
        return $true
    }

    if (Test-SkippedPath -Path $File.FullName) {
        return $true
    }

    return $false
}

function Get-ReplacedName {
    param([Parameter(Mandatory = $true)][string]$Name)

    return Get-ReplacedText -Text $Name
}

function Get-ReplacedText {
    param([Parameter(Mandatory = $true)][string]$Text)

    if ($Replacements.Count -eq 0) {
        return $Text
    }

    $Pattern = ($Replacements |
        Sort-Object { $_.From.Length } -Descending |
        ForEach-Object { [System.Text.RegularExpressions.Regex]::Escape($_.From) }) -join '|'

    $Evaluator = [System.Text.RegularExpressions.MatchEvaluator]{
        param([System.Text.RegularExpressions.Match]$Match)

        foreach ($Replacement in $Replacements) {
            if ($Replacement.From -ceq $Match.Value) {
                return $Replacement.To
            }
        }

        return $Match.Value
    }

    return [System.Text.RegularExpressions.Regex]::Replace($Text, $Pattern, $Evaluator)
}

function Assert-AvailableTargetPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    if ((Test-Path -LiteralPath $TargetPath) -and
        -not [string]::Equals($SourcePath, $TargetPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $RelativeSource = Get-RelativePath -Path $SourcePath
        $RelativeTarget = $TargetPath.Substring($Root.Length).TrimStart('\', '/')
        throw "Cannot rename '$RelativeSource' because '$RelativeTarget' already exists."
    }
}

function Update-TextFiles {
    $Updated = New-Object System.Collections.Generic.List[string]

    $Files = Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        Where-Object { -not (Test-SkippedTextFile -File $_) }

    foreach ($File in $Files) {
        $RelativePath = Get-RelativePath -Path $File.FullName
        $Before = [System.IO.File]::ReadAllText($File.FullName)
        $After = Get-ReplacedText -Text $Before

        if ($After -ne $Before) {
            if ($PSCmdlet.ShouldProcess($RelativePath, 'replace template names')) {
                [System.IO.File]::WriteAllText($File.FullName, $After, $Utf8NoBom)
            }
            $Updated.Add($RelativePath) | Out-Null
        }
    }

    return $Updated
}

function Update-ManifestFiles {
    $Updated = New-Object System.Collections.Generic.List[string]

    $ManifestFiles = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.json' -Force |
        Where-Object {
            -not (Test-SkippedTextFile -File $_) -and
            ((Get-RelativePath -Path $_.FullName) -split '[\\/]') -contains 'modPublish'
        }

    foreach ($File in $ManifestFiles) {
        $RelativePath = Get-RelativePath -Path $File.FullName

        try {
            $Text = [System.IO.File]::ReadAllText($File.FullName)
            $Json = $Text | ConvertFrom-Json
        }
        catch {
            Write-Warning "Skipping invalid json: $RelativePath"
            continue
        }

        $Changed = $false
        $PropertyNames = @($Json.PSObject.Properties.Name)

        if ($PropertyNames -contains 'id' -and $Json.id -ne $ModId) {
            $Json.id = $ModId
            $Changed = $true
        }

        if ($PropertyNames -contains 'name' -and $Json.name -ne $DisplayName) {
            $Json.name = $DisplayName
            $Changed = $true
        }

        if ($Author -and $PropertyNames -contains 'author' -and $Json.author -ne $Author) {
            $Json.author = $Author
            $Changed = $true
        }

        if ($Description -and $PropertyNames -contains 'description' -and $Json.description -ne $Description) {
            $Json.description = $Description
            $Changed = $true
        }

        if ($Changed) {
            if ($PSCmdlet.ShouldProcess($RelativePath, 'update mod manifest metadata')) {
                $Output = $Json | ConvertTo-Json -Depth 20
                [System.IO.File]::WriteAllText($File.FullName, $Output + [Environment]::NewLine, $Utf8NoBom)
            }
            $Updated.Add($RelativePath) | Out-Null
        }
    }

    return $Updated
}

function Rename-TemplatePaths {
    $Renamed = New-Object System.Collections.Generic.List[string]

    $Files = Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        Where-Object { -not (Test-SkippedRenameFile -File $_) }

    foreach ($File in $Files) {
        $NewName = Get-ReplacedName -Name $File.Name
        if ($NewName -eq $File.Name) {
            continue
        }

        $RelativePath = Get-RelativePath -Path $File.FullName
        $TargetPath = Join-Path $File.DirectoryName $NewName
        Assert-AvailableTargetPath -SourcePath $File.FullName -TargetPath $TargetPath

        if ($PSCmdlet.ShouldProcess($RelativePath, "rename to '$NewName'")) {
            Rename-Item -LiteralPath $File.FullName -NewName $NewName
        }

        $Renamed.Add("$RelativePath -> $NewName") | Out-Null
    }

    $Directories = Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force |
        Where-Object { -not (Test-SkippedPath -Path $_.FullName) } |
        Sort-Object { ($_.FullName -split '[\\/]').Count } -Descending

    foreach ($Directory in $Directories) {
        if (-not (Test-Path -LiteralPath $Directory.FullName)) {
            continue
        }

        $NewName = Get-ReplacedName -Name $Directory.Name
        if ($NewName -eq $Directory.Name) {
            continue
        }

        $RelativePath = Get-RelativePath -Path $Directory.FullName
        $TargetPath = Join-Path $Directory.Parent.FullName $NewName
        Assert-AvailableTargetPath -SourcePath $Directory.FullName -TargetPath $TargetPath

        if ($PSCmdlet.ShouldProcess($RelativePath, "rename to '$NewName'")) {
            Rename-Item -LiteralPath $Directory.FullName -NewName $NewName
        }

        $Renamed.Add("$RelativePath -> $NewName") | Out-Null
    }

    return $Renamed
}

$UpdatedTextFiles = Update-TextFiles
$UpdatedManifestFiles = Update-ManifestFiles
$RenamedPaths = Rename-TemplatePaths

Write-Host "Template customization complete."
Write-Host "Root: $Root"
Write-Host "Text files updated: $($UpdatedTextFiles.Count)"
foreach ($Path in $UpdatedTextFiles) {
    Write-Host "  $Path"
}

Write-Host "Manifest files updated: $($UpdatedManifestFiles.Count)"
foreach ($Path in $UpdatedManifestFiles) {
    Write-Host "  $Path"
}

Write-Host "Paths renamed: $($RenamedPaths.Count)"
foreach ($Path in $RenamedPaths) {
    Write-Host "  $Path"
}

Write-Host 'Skipped csproj content replacement; project file names are still renamed.'
Write-Host 'Use -WhatIf to preview changes before applying them.'
