param([string]$OutDir, [string]$PreviewDir)
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
New-Item -ItemType Directory -Force $OutDir, $PreviewDir | Out-Null

function C([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function LinBrush($a, $b, [double]$x1=0, [double]$y1=0, [double]$x2=0, [double]$y2=1) {
    $g = New-Object System.Windows.Media.LinearGradientBrush((C $a), (C $b), (New-Object System.Windows.Point $x1,$y1), (New-Object System.Windows.Point $x2,$y2)); $g.Freeze(); $g }
function RadBrush($center, $edge, [double]$cx=0.5, [double]$cy=0.5) {
    $g = New-Object System.Windows.Media.RadialGradientBrush((C $center), (C $edge)); $g.GradientOrigin = New-Object System.Windows.Point $cx,$cy; $g.Center = New-Object System.Windows.Point $cx,$cy; $g.Freeze(); $g }
function Rect([double]$x,[double]$y,[double]$w,[double]$h) { New-Object System.Windows.Rect $x,$y,$w,$h }
function Pt([double]$x,[double]$y) { New-Object System.Windows.Point $x,$y }

# Draws the icon on a 256-unit design grid; $detail drops fine detail at small sizes.
function Draw-Icon([System.Windows.Media.DrawingContext]$dc, [int]$px) {
    $detail = if ($px -ge 48) { 2 } elseif ($px -ge 24) { 1 } else { 0 }

    # --- App window (the remote desktop) ---------------------------------------------------------
    # Small sizes use more of the canvas so the shapes stay legible.
    $win = if ($detail -eq 0) { Rect 4 22 220 176 } else { Rect 20 34 196 156 }
    $r = if ($detail -eq 0) { 30 } else { 22 }
    # soft contact shadow
    foreach ($i in 1..6) {
        $a = [byte](14 - $i * 2)
        $sb = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb($a, 0, 32, 80))
        $dc.DrawRoundedRectangle($sb, $null, (Rect ($win.X - $i/2) ($win.Y + 2 + $i) ($win.Width + $i) ($win.Height + $i/2)), $r + $i/2, $r + $i/2)
    }
    $dc.DrawRoundedRectangle((LinBrush '#FF2B88D8' '#FF0F5BB0'), $null, $win, $r, $r)

    # screen inset (content area)
    $inset = if ($detail -eq 0) { 18 } else { 14 }
    $top = if ($detail -eq 0) { 34 } else { 30 }
    $screen = Rect ($win.X + $inset) ($win.Y + $top) ($win.Width - 2*$inset) ($win.Height - $top - $inset)
    $dc.DrawRoundedRectangle((LinBrush '#FFF3F9FF' '#FFCFE4F8'), $null, $screen, 8, 8)

    if ($detail -ge 1) {
        # title-bar "caption dots", the window cue
        $dotB = New-Object System.Windows.Media.SolidColorBrush (C '#CCFFFFFF')
        foreach ($k in 0..2) { $dc.DrawEllipse($dotB, $null, (Pt ($win.X + 24 + $k*15) ($win.Y + 16)), 4.2, 4.2) }
    }
    if ($detail -ge 2) {
        # faint content lines on the screen
        $lineB = New-Object System.Windows.Media.SolidColorBrush (C '#3D0F5BB0')
        $dc.DrawRoundedRectangle($lineB, $null, (Rect ($screen.X + 14) ($screen.Y + 16) 70 9), 4.5, 4.5)
        $dc.DrawRoundedRectangle($lineB, $null, (Rect ($screen.X + 14) ($screen.Y + 34) 48 9), 4.5, 4.5)
    }
    # top highlight on the window frame
    $hl = New-Object System.Windows.Media.Pen((LinBrush '#66FFFFFF' '#00FFFFFF'), 2)
    $dc.DrawRoundedRectangle($null, $hl, (Rect ($win.X + 1) ($win.Y + 1) ($win.Width - 2) ($win.Height - 2)), $r - 1, $r - 1)

    # --- Citrus slice overlapping the bottom-right corner -------------------------------------------
    $c = if ($detail -eq 0) { Pt 178 178 } else { Pt 178 182 }
    $R = if ($detail -eq 0) { 74 } else { 64 }
    foreach ($i in 1..7) {
        $a = [byte](22 - $i * 3)
        $sb = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb($a, 90, 30, 0))
        $dc.DrawEllipse($sb, $null, (Pt $c.X ($c.Y + 2 + $i*0.8)), $R + $i*0.7, $R + $i*0.7)
    }
    # rind
    $dc.DrawEllipse((LinBrush '#FFFF9F2E' '#FFE8590C' 0.2 0 0.8 1), $null, $c, $R, $R)
    # pith ring
    $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush (C '#FFFFF4DC')), $null, $c, $R - 8, $R - 8)
    # pith fills the whole inside; wedges sit on top
    $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush (C '#FFFFF4DC')), $null, $c, $R - 8, $R - 8)
    # flesh: separate juicy wedges with rounded corners, pith showing between them (a citrus slice,
    # not a wheel). Tiny sizes get a plain disc; fine gaps would just turn to mush.
    $segments = if ($detail -eq 0) { 0 } elseif ($detail -eq 1) { 7 } else { 9 }
    $flesh = RadBrush '#FFFFDA77' '#FFFF9E1B' 0.45 0.40
    if ($segments -eq 0) {
        $dc.DrawEllipse($flesh, $null, $c, $R - 14, $R - 14)
    } else {
        # Each wedge: a pie slice pulled in from its neighbours, then "inflated" by a round-joined stroke in
        # the same flesh colour, which rounds every corner into a soft teardrop. Pith shows through the gaps.
        $round = if ($detail -eq 1) { 6 } else { 5 }
        $fleshPen = New-Object System.Windows.Media.Pen($flesh, $round)
        $fleshPen.LineJoin = 'Round'
        $rOut = $R - 13 - $round / 2
        $gapUnits = $(if ($detail -eq 1) { 5 } else { 4 }) / 2 + $round / 2   # half-gap at the rim, before inflation
        $gapA = $gapUnits / $rOut
        for ($k = 0; $k -lt $segments; $k++) {
            $a0 = [Math]::PI * 2 * $k / $segments - [Math]::PI / 2 + $gapA
            $a1 = [Math]::PI * 2 * ($k + 1) / $segments - [Math]::PI / 2 - $gapA
            $mid = ($a0 + $a1) / 2
            # the inner tip sits far enough out that neighbouring wedges don't touch at the centre
            $rTip = ($gapUnits + 0.5) / [Math]::Sin([Math]::PI / $segments)
            $g = New-Object System.Windows.Media.StreamGeometry
            $ctx = $g.Open()
            $ctx.BeginFigure((Pt ($c.X + [Math]::Cos($mid) * $rTip) ($c.Y + [Math]::Sin($mid) * $rTip)), $true, $true)
            $ctx.LineTo((Pt ($c.X + [Math]::Cos($a0) * $rOut) ($c.Y + [Math]::Sin($a0) * $rOut)), $true, $true)
            $ctx.ArcTo((Pt ($c.X + [Math]::Cos($a1) * $rOut) ($c.Y + [Math]::Sin($a1) * $rOut)),
                       (New-Object System.Windows.Size $rOut, $rOut), 0, $false, 'Clockwise', $true, $true)
            $ctx.Close(); $g.Freeze()
            $dc.DrawGeometry($flesh, $fleshPen, $g)
        }
    }
    if ($detail -ge 2) {
        # glossy highlight, top-left of the slice
        $gl = RadBrush '#59FFFFFF' '#00FFFFFF' 0.5 0.5
        $dc.DrawEllipse($gl, $null, (Pt ($c.X - 22) ($c.Y - 24)), 26, 18)
    }
}

function Render([int]$px) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($px / 256.0), ($px / 256.0)))
    Draw-Icon $dc $px
    $dc.Pop(); $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $px, $px, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream; $enc.Save($ms); ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = Render $s }
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

# Preview sheet: every size at 1:1 on light and dark backgrounds, plus the 256 large.
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
[IO.File]::WriteAllBytes((Join-Path $PreviewDir 'icon-256.png'), $pngs[256])
"ok"
