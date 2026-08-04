[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = "Copy")]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$InputPath,

    [Parameter(ParameterSetName = "Copy")]
    [string]$OutputDir = "",

    [Parameter(ParameterSetName = "InPlace")]
    [switch]$InPlace,

    [switch]$Recursive,
    [switch]$Overwrite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath([string]$PathValue) {
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $PathValue))
}

if (-not ("ProjectedForegroundPngCenter" -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing.dll -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public sealed class ProjectedForegroundCenterResult
{
    public int WidthPx;
    public int HeightPx;
    public int BoundsLeft;
    public int BoundsTop;
    public int BoundsWidth;
    public int BoundsHeight;
    public int OffsetXPx;
    public int OffsetYPx;
    public bool ForegroundFound;
}

public static class ProjectedForegroundPngCenter
{
    public static ProjectedForegroundCenterResult Process(string inputPath, string outputPath)
    {
        using (var source = new Bitmap(inputPath))
        using (var rgb = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
        {
            using (var graphics = Graphics.FromImage(rgb))
            {
                graphics.Clear(Color.White);
                graphics.DrawImageUnscaled(source, 0, 0);
            }

            var result = FindBounds(rgb);
            var offsetX = 0;
            var offsetY = 0;
            if (result.ForegroundFound)
            {
                var sourceCenterX = result.BoundsLeft + (result.BoundsWidth - 1) / 2.0;
                var sourceCenterY = result.BoundsTop + (result.BoundsHeight - 1) / 2.0;
                var canvasCenterX = (rgb.Width - 1) / 2.0;
                var canvasCenterY = (rgb.Height - 1) / 2.0;
                offsetX = (int)Math.Round(canvasCenterX - sourceCenterX, MidpointRounding.ToEven);
                offsetY = (int)Math.Round(canvasCenterY - sourceCenterY, MidpointRounding.ToEven);

                var boundsRight = result.BoundsLeft + result.BoundsWidth;
                var boundsBottom = result.BoundsTop + result.BoundsHeight;
                offsetX = Math.Max(-result.BoundsLeft, Math.Min(rgb.Width - boundsRight, offsetX));
                offsetY = Math.Max(-result.BoundsTop, Math.Min(rgb.Height - boundsBottom, offsetY));
            }

            result.OffsetXPx = offsetX;
            result.OffsetYPx = offsetY;
            using (var output = new Bitmap(rgb.Width, rgb.Height, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(output))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImageUnscaled(rgb, offsetX, offsetY);
                }
                output.Save(outputPath, ImageFormat.Png);
            }
            return result;
        }
    }

    private static ProjectedForegroundCenterResult FindBounds(Bitmap bitmap)
    {
        var result = new ProjectedForegroundCenterResult
        {
            WidthPx = bitmap.Width,
            HeightPx = bitmap.Height
        };
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var minX = bitmap.Width;
            var minY = bitmap.Height;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = row + x * 3;
                    var b = bytes[index];
                    var g = bytes[index + 1];
                    var r = bytes[index + 2];
                    var distanceFromWhite = (255 - r) + (255 - g) + (255 - b);
                    var maxChannel = Math.Max(r, Math.Max(g, b));
                    var minChannel = Math.Min(r, Math.Min(g, b));
                    if (distanceFromWhite < 18 && maxChannel - minChannel < 8) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX >= minX && maxY >= minY)
            {
                result.ForegroundFound = true;
                result.BoundsLeft = minX;
                result.BoundsTop = minY;
                result.BoundsWidth = maxX - minX + 1;
                result.BoundsHeight = maxY - minY + 1;
            }
            return result;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
"@
}

$resolvedInput = Resolve-AbsolutePath $InputPath
if (-not (Test-Path -LiteralPath $resolvedInput)) {
    throw "InputPath does not exist: $resolvedInput"
}

$inputItem = Get-Item -LiteralPath $resolvedInput
if ($inputItem.PSIsContainer) {
    $inputBase = $inputItem.FullName.TrimEnd('\')
    $pngFiles = @(Get-ChildItem -LiteralPath $inputBase -File -Filter *.png -Recurse:$Recursive | Sort-Object FullName)
} else {
    if ($inputItem.Extension -ine ".png") {
        throw "InputPath must be a PNG file or a directory containing PNG files."
    }
    $inputBase = $inputItem.Directory.FullName.TrimEnd('\')
    $pngFiles = @($inputItem)
}

if ($pngFiles.Count -eq 0) {
    throw "No PNG files found under: $resolvedInput"
}

if (-not $InPlace) {
    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $inputParent = Split-Path -Parent $inputBase
        $inputLeaf = Split-Path -Leaf $inputBase
        $OutputDir = Join-Path $inputParent ($inputLeaf + "_recentered")
    }
    $resolvedOutput = Resolve-AbsolutePath $OutputDir
    New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
} else {
    $resolvedOutput = $inputBase
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($file in $pngFiles) {
    $relativePath = if ($inputItem.PSIsContainer) {
        $file.FullName.Substring($inputBase.Length).TrimStart('\')
    } else {
        $file.Name
    }
    $destination = if ($InPlace) { $file.FullName } else { Join-Path $resolvedOutput $relativePath }
    $destinationDir = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null

    if (-not $InPlace -and (Test-Path -LiteralPath $destination) -and -not $Overwrite) {
        $rows.Add([pscustomobject]@{
            Status = "skipped_existing"; InputPath = $file.FullName; OutputPath = $destination
            WidthPx = ""; HeightPx = ""; BoundsLeft = ""; BoundsTop = ""
            BoundsWidth = ""; BoundsHeight = ""; OffsetXPx = ""; OffsetYPx = ""
        })
        continue
    }

    $temporaryOutput = if ($InPlace) {
        Join-Path $destinationDir (([System.IO.Path]::GetFileNameWithoutExtension($destination)) + ".recenter_tmp_" + [guid]::NewGuid().ToString("N") + ".png")
    } else {
        $destination
    }

    if ($PSCmdlet.ShouldProcess($file.FullName, "Center projected model on PNG canvas")) {
        try {
            $result = [ProjectedForegroundPngCenter]::Process($file.FullName, $temporaryOutput)
            if ($InPlace) {
                Move-Item -LiteralPath $temporaryOutput -Destination $destination -Force
            }
            $rows.Add([pscustomobject]@{
                Status = if ($result.ForegroundFound) { "ok" } else { "no_foreground" }
                InputPath = $file.FullName
                OutputPath = $destination
                WidthPx = $result.WidthPx
                HeightPx = $result.HeightPx
                BoundsLeft = $result.BoundsLeft
                BoundsTop = $result.BoundsTop
                BoundsWidth = $result.BoundsWidth
                BoundsHeight = $result.BoundsHeight
                OffsetXPx = $result.OffsetXPx
                OffsetYPx = $result.OffsetYPx
            })
        } finally {
            if ($InPlace -and (Test-Path -LiteralPath $temporaryOutput)) {
                Remove-Item -LiteralPath $temporaryOutput -Force
            }
        }
    }
}

$manifestDir = if ($InPlace) { Join-Path $inputBase "recenter_manifests" } else { Join-Path $resolvedOutput "manifests" }
New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
$manifestPath = Join-Path $manifestDir ("projected_foreground_recenter_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".csv")
$rows | Export-Csv -LiteralPath $manifestPath -NoTypeInformation -Encoding UTF8
$rows | Format-Table Status,WidthPx,HeightPx,BoundsLeft,BoundsTop,BoundsWidth,BoundsHeight,OffsetXPx,OffsetYPx,OutputPath -AutoSize
Write-Host "Recenter manifest: $manifestPath"
