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
    [string]$DrawingTemplate = "",
    [string]$PdfToCairo = "",
    [int]$MaxProcessed = 2147483647,
    [switch]$Recursive,
    [switch]$Visible,
    [switch]$KeepSolidWorksOpen,
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
    $ToolExe = Join-Path $repoRoot "bin\SolidWorksDatasetPrep.exe"
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

    Write-Host "Default executable not found. Building it from the local SOLIDWORKS installation..."
    & $buildScript
    if (-not (Test-Path -LiteralPath $ToolExe -PathType Leaf)) {
        throw "Build completed without creating the expected executable: $ToolExe"
    }
}
if ($PSCmdlet.ParameterSetName -eq "Single") {
    if (-not (Test-Path -LiteralPath $InputFile -PathType Leaf)) { throw "Input STEP not found: $InputFile" }
} else {
    if (-not (Test-Path -LiteralPath $InputDir -PathType Container)) { throw "Input directory not found: $InputDir" }
}

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

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
            Write-Warning "Preferred $Label template '$PreferredName' was not found; using '$($fallback.Name)'. Output details may differ from the challenge data."
            return $fallback.FullName
        }
    }

    throw "No $Label template ($Extension) found. Pass -TemplateDir or an explicit template path."
}

$resolvedPart = Resolve-TemplateFile $PartTemplate $config.templates.preferred_part ".prtdot" "part"
$resolvedAssembly = Resolve-TemplateFile $AssemblyTemplate $config.templates.preferred_assembly ".asmdot" "assembly"
$resolvedDrawing = Resolve-TemplateFile $DrawingTemplate $config.templates.preferred_drawing_seed ".drwdot" "drawing seed"

if ([string]::IsNullOrWhiteSpace($PdfToCairo)) {
    $pdfCommand = Get-Command pdftocairo.exe -ErrorAction SilentlyContinue
    $PdfToCairo = if ($pdfCommand) { $pdfCommand.Source } else { "pdftocairo.exe" }
}

$toolArgs = @(
    "--output-root", (Resolve-AbsolutePath $OutputRoot),
    "--drawing-model-target-max-dimension", ([double]$config.normalization.drawing_model_target_max_dimension_m).ToString("R", $invariant),
    "--normalized-step-target-max-dimension", ([double]$config.normalization.normalized_step_target_max_dimension_m).ToString("R", $invariant),
    "--drawing-scale", ([double]$config.drawing.drawing_scale).ToString("R", $invariant),
    "--max-processed", $MaxProcessed.ToString($invariant),
    "--part-template", $resolvedPart,
    "--assembly-template", $resolvedAssembly,
    "--drawing-template", $resolvedDrawing,
    "--pdftocairo", $PdfToCairo,
    "--close-when-done"
)

if ($PSCmdlet.ParameterSetName -eq "Single") {
    $toolArgs += @("--input-file", (Resolve-AbsolutePath $InputFile))
} else {
    $toolArgs += @("--input-dir", (Resolve-AbsolutePath $InputDir))
}
if ($Recursive) { $toolArgs += "--recursive" }
if ($Visible) { $toolArgs += "--visible" }
if ($KeepSolidWorksOpen) {
    $toolArgs = @($toolArgs | Where-Object { $_ -ne "--close-when-done" })
}

$resolved = [ordered]@{
    executable = Resolve-AbsolutePath $ToolExe
    input = if ($PSCmdlet.ParameterSetName -eq "Single") { Resolve-AbsolutePath $InputFile } else { Resolve-AbsolutePath $InputDir }
    output_root = Resolve-AbsolutePath $OutputRoot
    drawing_model_target_max_dimension_m = [double]$config.normalization.drawing_model_target_max_dimension_m
    normalized_step_target_max_dimension_m = [double]$config.normalization.normalized_step_target_max_dimension_m
    drawing_scale = [double]$config.drawing.drawing_scale
    projection = $config.drawing.projection
    drawing_view_display_mode = $config.drawing.drawing_view_display_mode
    tangent_edges = $config.drawing.tangent_edges
    drawing_layer_name_policy = $config.drawing.layer_name_policy
    part_template = $resolvedPart
    assembly_template = $resolvedAssembly
    drawing_seed_template = $resolvedDrawing
    pdftocairo = $PdfToCairo
}

$resolved | ConvertTo-Json -Depth 4 | Write-Host
if ($DryRun) {
    Write-Host "Dry run complete. SOLIDWORKS was not started."
    return
}

& $ToolExe @toolArgs
$toolExitCode = $LASTEXITCODE
if ($toolExitCode -ne 0) {
    throw "Dataset preparation exited with code $toolExitCode. Inspect OutputRoot\logs and OutputRoot\manifests."
}

Write-Host "Dataset preparation complete: $(Resolve-AbsolutePath $OutputRoot)"
