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

$sourcePath = Join-Path $repoRoot "src\SolidWorksDatasetPrep.cs"
$redistDir = Join-Path $SolidWorksInstallDir "api\redist"
$sldWorksInterop = Join-Path $redistDir "SolidWorks.Interop.sldworks.dll"
$swConstInterop = Join-Path $redistDir "SolidWorks.Interop.swconst.dll"

foreach ($required in @($sourcePath, $CscPath, $sldWorksInterop, $swConstInterop)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required build input not found: $required"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$exePath = Join-Path $OutputDir "SolidWorksDatasetPrep.exe"
$compilerArgs = @(
    "/nologo",
    "/target:exe",
    "/platform:x64",
    "/optimize+",
    "/out:$exePath",
    "/reference:$sldWorksInterop",
    "/reference:$swConstInterop",
    $sourcePath
)

Write-Host "Compiling $sourcePath"
& $CscPath @compilerArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exePath)) {
    throw "C# compilation failed with exit code $LASTEXITCODE"
}

# These DLLs come from the local SOLIDWORKS installation. They are copied only
# into the ignored build directory and must not be redistributed.
Copy-Item -LiteralPath $sldWorksInterop -Destination $OutputDir -Force
Copy-Item -LiteralPath $swConstInterop -Destination $OutputDir -Force

$buildInfo = [ordered]@{
    built_at = (Get-Date).ToString("s")
    source = $sourcePath
    executable = $exePath
    solidworks_install_dir = $SolidWorksInstallDir
    compiler = $CscPath
}
$buildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir "build_info.json") -Encoding UTF8

Write-Host "Build complete: $exePath"
Write-Host "Do not redistribute the copied SolidWorks.Interop.*.dll files."
