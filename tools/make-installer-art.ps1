# Draws the installer's artwork and writes its licence page from LICENSE. Run it again if either changes.
#   powershell -File tools/make-installer-art.ps1
Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'installer\Assets'
New-Item -ItemType Directory -Force $out | Out-Null
$icon = [Drawing.Image]::FromFile((Join-Path $repo 'src\Citrium\Assets\Citrium.png'))

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object Drawing.Bitmap($w, $h, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::White)
    return $bmp, $g
}

# Welcome and finish pages: 493x312. Windows Installer's text starts 164 pixels in, so the panel stops there.
$bmp, $g = New-Canvas 493 312
$panel = New-Object Drawing.Rectangle(0, 0, 164, 312)
$fill = New-Object Drawing.Drawing2D.LinearGradientBrush($panel,
    [Drawing.Color]::FromArgb(0x2B, 0x88, 0xD8), [Drawing.Color]::FromArgb(0x0B, 0x3D, 0x7A), 75.0)
$g.FillRectangle($fill, $panel)
# Citrus slices drifting off the panel's corner.
$zest = [Drawing.Color]::FromArgb(0xFF, 0xA5, 0x2B)
foreach ($ring in @(@(150, 60), @(112, 110), @(76, 170))) {
    $pen = New-Object Drawing.Pen([Drawing.Color]::FromArgb($ring[1], $zest), 2)
    $d = $ring[0] * 2
    $g.DrawEllipse($pen, 164 - $ring[0] - 24, 312 - $ring[0] + 18, $d, $d)
    $pen.Dispose()
}
$g.DrawImage($icon, 34, 56, 96, 96)
$bmp.Save((Join-Path $out 'Dialog.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose(); $fill.Dispose()

# Top of the inner pages: 493x58. Their titles sit on the left, so the art keeps to the right.
$bmp, $g = New-Canvas 493 58
$band = New-Object Drawing.Rectangle(380, 0, 113, 58)
$fill = New-Object Drawing.Drawing2D.LinearGradientBrush($band,
    [Drawing.Color]::White, [Drawing.Color]::FromArgb(0xCF, 0xE6, 0xFA), 0.0)
$g.FillRectangle($fill, $band)
$g.DrawImage($icon, 437, 9, 40, 40)
$bmp.Save((Join-Path $out 'Banner.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose(); $fill.Dispose()
$icon.Dispose()

# The licence, as the RTF the installer shows.
$text = (Get-Content (Join-Path $repo 'LICENSE') -Raw) -replace "`r`n", "`n"
$escaped = $text.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}').Replace("`n", "\par`n")
$rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0\fnil Segoe UI;}}\f0\fs16`n$escaped`n}`n"
[IO.File]::WriteAllText((Join-Path $out 'License.rtf'), $rtf, (New-Object Text.ASCIIEncoding))
"Wrote $out"
