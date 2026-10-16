<#
.SYNOPSIS
  Draws the AirLens app icon (a camera lens on a dark rounded square) and writes a multi-size
  .ico: 16-64 px as 32-bit DIBs, which every Windows component reads, and 256 px as PNG.

.EXAMPLE
  powershell -File windows\installer\make-icon.ps1
#>
param([string]$Out = (Join-Path $PSScriptRoot "..\src\AirLens.App\Assets\AirLens.ico"))
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.PixelOffsetMode = "HighQuality"
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $c = { param($hex) [System.Drawing.ColorTranslator]::FromHtml($hex) }

    # Background tile.
    $tile = New-RoundedRect 8 8 240 240 56
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, 256)), (& $c "#223044"), (& $c "#0E1116"))
    $g.FillPath($bg, $tile)

    # Lens barrel: blue-to-teal ring.
    $ring = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF(40, 40)), (New-Object System.Drawing.PointF(216, 216)), (& $c "#4C8DFF"), (& $c "#3DD6C4"))
    $g.FillEllipse($ring, 40, 40, 176, 176)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush((& $c "#0A0D12"))), 64, 64, 128, 128)

    # Glass and iris.
    $glass = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF(84, 84)), (New-Object System.Drawing.PointF(172, 172)), (& $c "#35527A"), (& $c "#121A26"))
    $g.FillEllipse($glass, 84, 84, 88, 88)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush((& $c "#070A0E"))), 110, 110, 36, 36)

    # Reflection.
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 255, 255, 255))), 98, 96, 22, 22)
    $g.Dispose()
    $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, "ReadOnly", [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $maskRow = [int]([math]::Ceiling($w / 32.0) * 4)
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ms)
    # BITMAPINFOHEADER: height counts the XOR image plus the AND mask.
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($w * $h * 4 + $maskRow * $h)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $w * 4) }  # bottom-up BGRA
    $bw.Write((New-Object byte[] ($maskRow * $h)))                                   # alpha decides; mask all zero
    $bw.Flush()
    , $ms.ToArray()
}

$images = foreach ($s in 16, 20, 24, 32, 40, 48, 64, 256) {
    $bmp = Draw-Icon $s
    if ($s -eq 256) {
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = Get-DibBytes $bmp
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $bytes }
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [IO.File]::Create($Out)
$w = New-Object IO.BinaryWriter($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$i.Bytes.Length); $w.Write([int]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $w.Write($i.Bytes) }
$w.Dispose()
"wrote $Out ($((Get-Item $Out).Length) bytes, sizes $($images.Size -join ', '))"
