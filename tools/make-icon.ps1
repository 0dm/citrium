# Renders tools/icon.html at each icon size with headless Edge, then writes Citrium.png (256 px),
# a multi-resolution Citrium.ico, and a preview sheet of every size on light and dark backgrounds.
param([string]$OutDir, [string]$PreviewDir)
Add-Type -AssemblyName PresentationCore, WindowsBase
New-Item -ItemType Directory -Force $OutDir, $PreviewDir | Out-Null

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge is needed to render the icon.' }
$page = ([Uri](Join-Path $PSScriptRoot 'icon.html')).AbsoluteUri
$tmp = Join-Path ([IO.Path]::GetTempPath()) "citrium-icon-$PID"
New-Item -ItemType Directory -Force $tmp | Out-Null

# Edge won't make a window smaller than about 100 px, so every size is drawn in a 256 px window
# and cropped from the top-left corner.
function Render([int]$px) {
    $shot = Join-Path $tmp "$px.png"
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
        --default-background-color=00000000 --window-size=256,256 --screenshot="$shot" "$page`?size=$px" 2>&1 | Out-Null
    $src = [System.Windows.Media.Imaging.BitmapFrame]::Create([IO.MemoryStream]::new([IO.File]::ReadAllBytes($shot)))
    $crop = New-Object System.Windows.Media.Imaging.CroppedBitmap $src, (New-Object System.Windows.Int32Rect 0, 0, $px, $px)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($crop))
    $ms = New-Object IO.MemoryStream; $enc.Save($ms); ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = Render $s }
Remove-Item -Recurse -Force $tmp
[IO.File]::WriteAllBytes((Join-Path $OutDir 'Citrium.png'), $pngs[256])

# Multi-resolution ICO with PNG-compressed entries.
$fs = [IO.File]::Create((Join-Path $OutDir 'Citrium.ico')); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) { $d = $pngs[$s]; $b = [byte]($(if ($s -ge 256) { 0 } else { $s }))
    $w.Write($b); $w.Write($b); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$d.Length); $w.Write([uint32]$offset); $offset += $d.Length }
foreach ($s in $sizes) { $w.Write($pngs[$s]) }
$w.Close()

# Preview sheet: every size at 1:1 on light and dark backgrounds.
function C([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Rect([double]$x,[double]$y,[double]$w,[double]$h) { New-Object System.Windows.Rect $x,$y,$w,$h }
$sheetW = 720; $sheetH = 420
$dv = New-Object System.Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
$dc.DrawRectangle((New-Object System.Windows.Media.SolidColorBrush (C '#FFF3F3F3')), $null, (Rect 0 0 $sheetW ($sheetH/2)))
$dc.DrawRectangle((New-Object System.Windows.Media.SolidColorBrush (C '#FF202020')), $null, (Rect 0 ($sheetH/2) $sheetW ($sheetH/2)))
foreach ($row in 0, 1) {
    $x = 20
    foreach ($s in 16, 20, 24, 32, 48, 64, 128) {
        $bi = New-Object System.Windows.Media.Imaging.BitmapImage; $bi.BeginInit(); $bi.StreamSource = New-Object IO.MemoryStream(,$pngs[$s]); $bi.EndInit()
        $dc.DrawImage($bi, (Rect $x ($row * $sheetH/2 + (210 - $s)/2) $s $s)); $x += $s + 22
    }
}
$dc.Close()
$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $sheetW, $sheetH, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32); $rtb.Render($dv)
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder; $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
$f = [IO.File]::Create((Join-Path $PreviewDir 'icon-sizes.png')); $enc.Save($f); $f.Close()
"ok"
