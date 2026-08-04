[CmdletBinding(DefaultParameterSetName = "Batch")]
param(
    [Parameter(Mandatory, ParameterSetName = "Single")]
    [string]$InputFile,

    [Parameter(Mandatory, ParameterSetName = "Batch")]
    [string]$InputDir,

    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [string]$ConfigPath = "",
    [string]$ToolExe = "",
    [string]$TemplateDir = "",
    [string]$PartTemplate = "",
    [string]$AssemblyTemplate = "",
    [int]$Width = 0,
    [int]$Height = 0,
    [int]$MaxProcessed = 2147483647,
    [switch]$Recursive,
    [switch]$Visible,
    [switch]$KeepSolidWorksOpen,
    [switch]$KeepBmp,
    [switch]$SkipExisting,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$invocationDirectory = (Get-Location).Path

function Resolve-AbsolutePath {
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $script:invocationDirectory $Path))
}

$toolWasExplicit = -not [string]::IsNullOrWhiteSpace($ToolExe)
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $repoRoot "config\challenge_2026.json"
}
if ([string]::IsNullOrWhiteSpace($ToolExe)) {
    $ToolExe = Join-Path $repoRoot "bin\SolidWorksTransparentPerspectiveRender.exe"
}

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Configuration file not found: $ConfigPath"
}
if (-not (Test-Path -LiteralPath $ToolExe -PathType Leaf)) {
    if ($toolWasExplicit) {
        throw "Specified ToolExe not found: $ToolExe"
    }

    $buildScript = Join-Path $PSScriptRoot "build.ps1"
    if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
        throw "Default executable is missing and the build script was not found: $buildScript"
    }

    Write-Host "Default 3D renderer executable not found. Building it from the local SOLIDWORKS installation..."
    & $buildScript
    if (-not (Test-Path -LiteralPath $ToolExe -PathType Leaf)) {
        throw "Build completed without creating the expected executable: $ToolExe"
    }
}
if ($PSCmdlet.ParameterSetName -eq "Single") {
    if (-not (Test-Path -LiteralPath $InputFile -PathType Leaf)) { throw "Input model not found: $InputFile" }
} else {
    if (-not (Test-Path -LiteralPath $InputDir -PathType Container)) { throw "Input directory not found: $InputDir" }
}

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if ($Width -le 0) {
    $Width = if ($config.render_3d -and $config.render_3d.width_px) { [int]$config.render_3d.width_px } else { 1400 }
}
if ($Height -le 0) {
    $Height = if ($config.render_3d -and $config.render_3d.height_px) { [int]$config.render_3d.height_px } else { 1000 }
}

$templateDirs = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($TemplateDir)) {
    $templateDirs.Add((Resolve-AbsolutePath $TemplateDir))
} else {
    foreach ($candidate in @(
        (Join-Path $env:ProgramData "SOLIDWORKS\SOLIDWORKS 2025\templates"),
        (Join-Path $env:ProgramData "SolidWorks\SOLIDWORKS 2025\templates")
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Container) { $templateDirs.Add($candidate) }
    }
}

function Resolve-TemplateFile {
    param(
        [string]$ExplicitPath,
        [string]$PreferredName,
        [string]$Extension,
        [string]$Label
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) { throw "$Label template not found: $ExplicitPath" }
        return Resolve-AbsolutePath $ExplicitPath
    }

    foreach ($dir in $templateDirs) {
        $preferredPath = Join-Path $dir $PreferredName
        if (Test-Path -LiteralPath $preferredPath -PathType Leaf) { return $preferredPath }
    }

    foreach ($dir in $templateDirs) {
        $fallback = Get-ChildItem -LiteralPath $dir -File -Filter "*$Extension" -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
        if ($fallback) {
            Write-Warning "Preferred $Label template '$PreferredName' was not found; using '$($fallback.Name)'. STEP import details may differ from the challenge data."
            return $fallback.FullName
        }
    }

    throw "No $Label template ($Extension) found. Pass -TemplateDir or an explicit template path."
}

$resolvedPart = Resolve-TemplateFile $PartTemplate $config.templates.preferred_part ".prtdot" "part"
$resolvedAssembly = Resolve-TemplateFile $AssemblyTemplate $config.templates.preferred_assembly ".asmdot" "assembly"

$toolArgs = @(
    "--output-root", (Resolve-AbsolutePath $OutputRoot),
    "--width", $Width.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--height", $Height.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--max-processed", $MaxProcessed.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--part-template", $resolvedPart,
    "--assembly-template", $resolvedAssembly,
    "--close-when-done"
)

if ($PSCmdlet.ParameterSetName -eq "Single") {
    $toolArgs += @("--input-file", (Resolve-AbsolutePath $InputFile))
} else {
    $toolArgs += @("--input-dir", (Resolve-AbsolutePath $InputDir))
}
if ($Recursive) { $toolArgs += "--recursive" }
if ($Visible) { $toolArgs += "--visible" }
if ($KeepBmp) { $toolArgs += "--keep-bmp" }
if ($SkipExisting) { $toolArgs += "--skip-existing" }
if ($KeepSolidWorksOpen) {
    $toolArgs = @($toolArgs | Where-Object { $_ -ne "--close-when-done" })
}

$resolved = [ordered]@{
    executable = Resolve-AbsolutePath $ToolExe
    input = if ($PSCmdlet.ParameterSetName -eq "Single") { Resolve-AbsolutePath $InputFile } else { Resolve-AbsolutePath $InputDir }
    output_root = Resolve-AbsolutePath $OutputRoot
    render_output_root = Join-Path (Resolve-AbsolutePath $OutputRoot) "render_3D"
    width_px = $Width
    height_px = $Height
    view = "isometric"
    perspective = $true
    keep_raw_bmp = [bool]$KeepBmp
    styles = @(
        "transparent_shaded_edges_perspective",
        "hlg_perspective",
        "hlg_translucent_faces_perspective"
    )
    part_template = $resolvedPart
    assembly_template = $resolvedAssembly
}

$resolved | ConvertTo-Json -Depth 4 | Write-Host
if ($DryRun) {
    Write-Host "Dry run complete. SOLIDWORKS was not started."
    return
}

& $ToolExe @toolArgs
$toolExitCode = $LASTEXITCODE
if ($toolExitCode -ne 0) {
    throw "3D renderer exited with code $toolExitCode. Inspect OutputRoot\logs and OutputRoot\manifests."
}

Write-Host "3D transparent perspective rendering complete: $(Resolve-AbsolutePath $OutputRoot)"
