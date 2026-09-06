# Draws src/KotorCoop.Agent/Assets/icon.ico from scratch (no external image tool required):
# a dark badge with two crossed sabers, standing in for "two players, one game" until a real
# piece of art replaces it. Run once by hand; the .ico is checked in like any other asset, so
# players and CI never need to regenerate it.
#
#   pwsh tools/generate-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

$repo = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $repo 'src\KotorCoop.Agent\Assets'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$outPath = Join-Path $outDir 'icon.ico'

function New-IconFrame([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $navyDark = [System.Drawing.Color]::FromArgb(255, 15, 20, 38)
    $navyLight = [System.Drawing.Color]::FromArgb(255, 34, 44, 74)
    $rect = [System.Drawing.RectangleF]::new(0, 0, $size, $size)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $navyLight, $navyDark, 90.0)
    $g.FillEllipse($brush, 1, 1, $size - 2, $size - 2)
    $brush.Dispose()

    $ringWidth = [Math]::Max(1.0, $size / 24.0)
    $ringPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 90, 104, 150), $ringWidth)
    $g.DrawEllipse($ringPen, $ringWidth, $ringWidth, $size - 2 * $ringWidth, $size - 2 * $ringWidth)
    $ringPen.Dispose()

    # Two crossed sabers: one cyan (host), one amber (client) - "two players, one game".
    $bladeWidth = [Math]::Max(1.5, $size / 11.0)
    $inset = $size * 0.24
    $cyan = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 80, 220, 255), $bladeWidth)
    $cyan.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cyan.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $amber = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 176, 70), $bladeWidth)
    $amber.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $amber.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $g.DrawLine($amber, $inset, $inset, $size - $inset, $size - $inset)
    $g.DrawLine($cyan, $size - $inset, $inset, $inset, $size - $inset)

    $hiltSize = [Math]::Max(1.0, $size / 14.0)
    $far = $size - $inset
    $hiltBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 210, 214, 224))
    $g.FillEllipse($hiltBrush, $inset - $hiltSize / 2, $inset - $hiltSize / 2, $hiltSize, $hiltSize)
    $g.FillEllipse($hiltBrush, $far - $hiltSize / 2, $far - $hiltSize / 2, $hiltSize, $hiltSize)
    $g.FillEllipse($hiltBrush, $far - $hiltSize / 2, $inset - $hiltSize / 2, $hiltSize, $hiltSize)
    $g.FillEllipse($hiltBrush, $inset - $hiltSize / 2, $far - $hiltSize / 2, $hiltSize, $hiltSize)
    $hiltBrush.Dispose()
    $cyan.Dispose()
    $amber.Dispose()
    $g.Dispose()
    return $bmp
}

$sizes = 16, 32, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { New-IconFrame $_ }

$pngBytes = @()
foreach ($frame in $frames) {
    $ms = [System.IO.MemoryStream]::new()
    $frame.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes += , $ms.ToArray()
    $ms.Dispose()
    $frame.Dispose()
}

# Manual ICO container (PNG-compressed frames, supported since Windows Vista): a 6-byte
# ICONDIR header, one 16-byte ICONDIRENTRY per frame, then the frames' raw PNG bytes back
# to back. System.Drawing has no multi-resolution ICO encoder, so this is written by hand.
$stream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)      # reserved
$writer.Write([uint16]1)      # type = icon
$writer.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $byteSize = $pngBytes[$i].Length
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([byte]0)    # color count (0 = no palette)
    $writer.Write([byte]0)    # reserved
    $writer.Write([uint16]1)  # color planes
    $writer.Write([uint16]32) # bits per pixel
    $writer.Write([uint32]$byteSize)
    $writer.Write([uint32]$offset)
    $offset += $byteSize
}
foreach ($bytes in $pngBytes) { $writer.Write($bytes) }
$writer.Flush()

[System.IO.File]::WriteAllBytes($outPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

"wrote $outPath ($([math]::Round((Get-Item $outPath).Length / 1KB, 1)) KB, $($sizes.Count) sizes: $($sizes -join ', '))"
