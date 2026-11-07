# Draws Acme, the made-up company in a demo build: its logo and its app icons. Run it again if you change them.
#   powershell -File tools/make-demo-art.ps1
Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'src\Citrium\Demo\Assets'
New-Item -ItemType Directory -Force (Join-Path $out 'icons') | Out-Null

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object Drawing.Bitmap($w, $h, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    return $bmp, $g
}

function Rgb([string]$hex) { [Drawing.ColorTranslator]::FromHtml("#$hex") }

function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Points($coords) {
    $list = New-Object 'System.Collections.Generic.List[Drawing.PointF]'
    for ($i = 0; $i -lt $coords.Count; $i += 2) { $list.Add((New-Object Drawing.PointF([float]$coords[$i], [float]$coords[$i + 1]))) }
    return , $list.ToArray()
}

# The logo: two peaks, and the company's name in the theme's ink.
function Write-Logo([string]$file, [Drawing.Color]$ink) {
    $bmp, $g = New-Canvas 300 66
    $back = New-Object Drawing.SolidBrush((Rgb 'F97316'))
    $front = New-Object Drawing.SolidBrush((Rgb 'FBBF24'))
    $g.FillPolygon($back, (Points @(26, 62, 58, 4, 90, 62)))
    $g.FillPolygon($front, (Points @(2, 62, 28, 22, 54, 62)))
    $font = New-Object Drawing.Font('Segoe UI Semibold', 50, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('Acme', $font, (New-Object Drawing.SolidBrush($ink)), 96, -3)
    $g.Dispose()
    $bmp.Save((Join-Path $out $file), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Write-Logo 'logo-light.png' (Rgb '1F2937')
Write-Logo 'logo-dark.png' (Rgb 'F9FAFB')

# The icons: a coloured square and a simple white shape on a 256 pixel grid.
$white = [Drawing.Brushes]::White
function Stroke([float]$w) {
    $p = New-Object Drawing.Pen([Drawing.Color]::White, $w)
    $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'
    return $p
}
function Write-Icon([string]$name, [string]$from, [string]$to, [scriptblock]$draw) {
    $bmp, $g = New-Canvas 256 256
    $square = Rounded 8 8 240 240 52
    $rect = New-Object Drawing.Rectangle(0, 0, 256, 256)
    $g.FillPath((New-Object Drawing.Drawing2D.LinearGradientBrush($rect, (Rgb $from), (Rgb $to), 60.0)), $square)
    & $draw $g
    $g.Dispose()
    $bmp.Save((Join-Path $out "icons\$name.png"), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
function Monitor($g) {
    $g.DrawPath((Stroke 14), (Rounded 56 62 144 104 14))
    $g.DrawLine((Stroke 14), 128, 168, 128, 196)
    $g.DrawLine((Stroke 14), 94, 198, 162, 198)
}

Write-Icon 'engineering' '2F80ED' '10205A' { param($g) Monitor $g; $g.DrawLines((Stroke 12), (Points @(90, 94, 110, 114, 90, 134))); $g.DrawLine((Stroke 12), 124, 134, 160, 134) }
Write-Icon 'design' 'F26B5B' '5B2A86' { param($g) Monitor $g; $g.FillEllipse($white, 108, 94, 40, 40) }
Write-Icon 'finance' '1D8A7A' '0E2F4F' { param($g) Monitor $g; foreach ($b in @(@(92, 118), @(120, 100), @(148, 88))) { $g.FillRectangle($white, $b[0], $b[1], 16, 140 - $b[1]) } }
Write-Icon 'lab' '4B5563' '111827' { param($g) Monitor $g; $g.DrawLines((Stroke 11), (Points @(114, 90, 114, 112, 96, 140, 160, 140, 142, 112, 142, 90))) }
Write-Icon 'mail' '38BDF8' '2563EB' { param($g) $g.DrawPath((Stroke 14), (Rounded 50 74 156 110 14)); $g.DrawLines((Stroke 14), (Points @(56, 84, 128, 138, 200, 84))) }
Write-Icon 'calendar' 'FB7185' 'BE123C' { param($g) $g.DrawPath((Stroke 14), (Rounded 56 64 144 134 14)); $g.DrawLine((Stroke 14), 58, 106, 198, 106); foreach ($x in 92, 164) { $g.DrawLine((Stroke 14), $x, 48, $x, 76) }; foreach ($x in 88, 120, 152) { $g.FillEllipse($white, $x, 134, 16, 16) } }
Write-Icon 'docs' '60A5FA' '4338CA' { param($g) $g.DrawPath((Stroke 14), (Rounded 70 48 116 160 12)); foreach ($y in 92, 124, 156) { $g.DrawLine((Stroke 12), 98, $y, 158, $y) } }
Write-Icon 'chat' 'A78BFA' '6D28D9' { param($g) $g.DrawPath((Stroke 14), (Rounded 52 60 152 108 26)); $g.FillPolygon($white, (Points @(86, 160, 86, 206, 134, 164))); foreach ($x in 92, 120, 148) { $g.FillEllipse($white, $x, 106, 16, 16) } }
Write-Icon 'sheets' '4ADE80' '15803D' { param($g) $g.DrawPath((Stroke 14), (Rounded 54 60 148 136 12)); $g.DrawLine((Stroke 11), 56, 106, 200, 106); $g.DrawLine((Stroke 11), 56, 150, 200, 150); $g.DrawLine((Stroke 11), 128, 62, 128, 194) }
Write-Icon 'code' '818CF8' '312E81' { param($g) $g.DrawLines((Stroke 16), (Points @(100, 82, 56, 128, 100, 174))); $g.DrawLines((Stroke 16), (Points @(156, 82, 200, 128, 156, 174))); $g.DrawLine((Stroke 14), 140, 72, 116, 184) }
Write-Icon 'builds' 'FBBF24' 'D97706' { param($g) foreach ($b in @(@(64, 130), @(100, 90), @(136, 110), @(172, 62))) { $g.FillPath($white, (Rounded $b[0] $b[1] 22 (194 - $b[1]) 8)) } }
Write-Icon 'time' '2DD4BF' '0F766E' { param($g) $g.DrawEllipse((Stroke 14), 56, 56, 144, 144); $g.DrawLines((Stroke 14), (Points @(128, 88, 128, 130, 158, 148))) }
Write-Icon 'expenses' 'F472B6' 'A21CAF' { param($g) $g.DrawLines((Stroke 14), (Points @(74, 208, 74, 52, 182, 52, 182, 208, 164, 194, 146, 208, 128, 194, 110, 208, 92, 194, 74, 208))); foreach ($y in 92, 124, 154) { $g.DrawLine((Stroke 12), 102, $y, 154, $y) } }
Write-Icon 'support' 'FB923C' 'C2410C' { param($g) $g.DrawEllipse((Stroke 14), 52, 52, 152, 152); $g.DrawEllipse((Stroke 14), 96, 96, 64, 64); foreach ($l in @(@(74, 74, 104, 104), @(182, 74, 152, 104), @(74, 182, 104, 152), @(182, 182, 152, 152))) { $g.DrawLine((Stroke 14), $l[0], $l[1], $l[2], $l[3]) } }
Write-Icon 'rockets' '94A3B8' '334155' { param($g) $g.FillPolygon($white, (Points @(128, 40, 158, 98, 158, 168, 98, 168, 98, 98))); $g.FillPolygon($white, (Points @(98, 128, 66, 182, 98, 172))); $g.FillPolygon($white, (Points @(158, 128, 190, 182, 158, 172))); $g.FillPolygon($white, (Points @(114, 178, 142, 178, 128, 216))) }
