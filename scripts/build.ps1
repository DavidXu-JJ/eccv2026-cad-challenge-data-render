[CmdletBinding()]
param(
    [string]$SolidWorksInstallDir = "",
    [string]$CscPath = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($SolidWorksInstallDir)) {
    $SolidWorksInstallDir = Join-Path $env:ProgramFiles "SOLIDWORKS Corp\SOLIDWORKS"
}
if ([string]::IsNullOrWhiteSpace($CscPath)) {
    $CscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "bin"
}

$datasetSourcePath = Join-Path $repoRoot "src\SolidWorksDatasetPrep.cs"
$transparentSourcePath = Join-Path $repoRoot "src\SolidWorksTransparentPerspectiveRender.cs"
$redistDir = Join-Path $SolidWorksInstallDir "api\redist"
$sldWorksInterop = Join-Path $redistDir "SolidWorks.Interop.sldworks.dll"
$swConstInterop = Join-Path $redistDir "SolidWorks.Interop.swconst.dll"
$frameworkDir = Split-Path -Parent $CscPath
$systemDrawing = Join-Path $frameworkDir "System.Drawing.dll"

foreach ($required in @($datasetSourcePath, $transparentSourcePath, $CscPath, $sldWorksInterop, $swConstInterop, $systemDrawing)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required build input not found: $required"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function Compile-Exe {
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$ExePath,
        [string[]]$ExtraReferences = @()
    )

    $compilerArgs = @(
        "/nologo",
        "/target:exe",
        "/platform:x64",
        "/optimize+",
        "/out:$ExePath",
        "/reference:$sldWorksInterop",
        "/reference:$swConstInterop"
    )
    foreach ($reference in $ExtraReferences) {
        $compilerArgs += "/reference:$reference"
    }
    $compilerArgs += $SourcePath

    Write-Host "Compiling $SourcePath"
    & $CscPath @compilerArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $ExePath)) {
        throw "C# compilation failed with exit code $LASTEXITCODE"
    }
}

$datasetExePath = Join-Path $OutputDir "SolidWorksDatasetPrep.exe"
$transparentExePath = Join-Path $OutputDir "SolidWorksTransparentPerspectiveRender.exe"
Compile-Exe -SourcePath $datasetSourcePath -ExePath $datasetExePath
Compile-Exe -SourcePath $transparentSourcePath -ExePath $transparentExePath -ExtraReferences @($systemDrawing)

# These DLLs come from the local SOLIDWORKS installation. They are copied only
# into the ignored build directory and must not be redistributed.
Copy-Item -LiteralPath $sldWorksInterop -Destination $OutputDir -Force
Copy-Item -LiteralPath $swConstInterop -Destination $OutputDir -Force

$buildInfo = [ordered]@{
    built_at = (Get-Date).ToString("s")
    dataset_source = $datasetSourcePath
    dataset_executable = $datasetExePath
    transparent_perspective_source = $transparentSourcePath
    transparent_perspective_executable = $transparentExePath
    solidworks_install_dir = $SolidWorksInstallDir
    compiler = $CscPath
}
$buildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir "build_info.json") -Encoding UTF8

Write-Host "Build complete:"
Write-Host "  $datasetExePath"
Write-Host "  $transparentExePath"
Write-Host "Do not redistribute the copied SolidWorks.Interop.*.dll files."
