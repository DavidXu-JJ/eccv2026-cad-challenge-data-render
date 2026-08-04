[CmdletBinding()]
param(
    [string]$SolidWorksInstallDir = "",
    [string]$TemplateDir = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SolidWorksInstallDir)) {
    $SolidWorksInstallDir = Join-Path $env:ProgramFiles "SOLIDWORKS Corp\SOLIDWORKS"
}
if ([string]::IsNullOrWhiteSpace($TemplateDir)) {
    $TemplateDir = Join-Path $env:ProgramData "SOLIDWORKS\SOLIDWORKS 2025\templates"
}

$checks = @(
    [pscustomobject]@{ Name = "SOLIDWORKS executable"; Path = (Join-Path $SolidWorksInstallDir "SLDWORKS.exe") },
    [pscustomobject]@{ Name = "sldworks interop"; Path = (Join-Path $SolidWorksInstallDir "api\redist\SolidWorks.Interop.sldworks.dll") },
    [pscustomobject]@{ Name = "swconst interop"; Path = (Join-Path $SolidWorksInstallDir "api\redist\SolidWorks.Interop.swconst.dll") },
    [pscustomobject]@{ Name = ".NET Framework C# compiler"; Path = (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe") },
    [pscustomobject]@{ Name = "part template"; Path = (Join-Path $TemplateDir "gb_part.prtdot") },
    [pscustomobject]@{ Name = "assembly template"; Path = (Join-Path $TemplateDir "gb_assembly.asmdot") },
    [pscustomobject]@{ Name = "drawing seed template"; Path = (Join-Path $TemplateDir "gb_a4.drwdot") }
)

$results = foreach ($check in $checks) {
    [pscustomobject]@{
        Name = $check.Name
        Found = Test-Path -LiteralPath $check.Path -PathType Leaf
        Path = $check.Path
    }
}

$pdfToCairo = Get-Command pdftocairo.exe -ErrorAction SilentlyContinue
$results += [pscustomobject]@{
    Name = "pdftocairo (SVG fallback)"
    Found = $null -ne $pdfToCairo
    Path = if ($pdfToCairo) { $pdfToCairo.Source } else { "not on PATH" }
}

$results | Format-Table -AutoSize
if (@($results | Where-Object { -not $_.Found -and $_.Name -ne "pdftocairo (SVG fallback)" }).Count -gt 0) {
    throw "One or more required dependencies were not found. Override the install/template paths where needed."
}

Write-Host "Required dependencies found. pdftocairo is optional unless direct SVG export fails."
