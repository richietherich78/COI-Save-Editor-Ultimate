Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$outPath = Join-Path $PSScriptRoot '..\Assets\app.ico'
$dir = Split-Path $outPath -Parent
if (!(Test-Path $dir)) { New-Item $dir -ItemType Directory -Force | Out-Null }

# ── Shared palette ──
function C([byte]$r,[byte]$g,[byte]$b){ return [System.Windows.Media.Color]::FromRgb($r,$g,$b) }
function CA([byte]$a,[byte]$r,[byte]$g,[byte]$b){ return [System.Windows.Media.Color]::FromArgb($a,$r,$g,$b) }
function SB($c){ return New-Object System.Windows.Media.SolidColorBrush($c) }
$cBg1      = C 0x12 0x1a 0x2b
$cBg2      = C 0x08 0x0c 0x14
$cAccent   = C 0x58 0xa6 0xff
$cAccentLo = C 0x2a 0x6c 0xc4
$cGlow     = CA 0x30 0x58 0xa6 0xff
$cGreen    = C 0x3f 0xb9 0x50
$cGreenDk  = C 0x23 0x86 0x36
$cBorder   = C 0x30 0x3d 0x56
$cDark     = C 0x0a 0x0e 0x16

# ── Draw the icon (square, no text for small sizes) ──
function RenderIcon([int]$sz) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = $sz / 256.0

    # Background: radial gradient with a subtle blue hotspot
    $bgRadial = New-Object System.Windows.Media.RadialGradientBrush
    $bgRadial.Center = (New-Object System.Windows.Point(0.35, 0.30))
    $bgRadial.GradientOrigin = (New-Object System.Windows.Point(0.35, 0.30))
    $bgRadial.RadiusX = 0.8; $bgRadial.RadiusY = 0.8
    $bgRadial.GradientStops.Add((New-Object System.Windows.Media.GradientStop((C 0x18 0x24 0x3a), 0.0)))
    $bgRadial.GradientStops.Add((New-Object System.Windows.Media.GradientStop($cBg1, 0.5)))
    $bgRadial.GradientStops.Add((New-Object System.Windows.Media.GradientStop($cBg2, 1.0)))
    $bPen = New-Object System.Windows.Media.Pen((SB $cBorder), (3*$s))
    $pad = 6*$s
    $dc.DrawRoundedRectangle($bgRadial, $bPen, (New-Object System.Windows.Rect($pad,$pad,($sz-2*$pad),($sz-2*$pad))), (32*$s), (32*$s))

    # Subtle corner accent lines (circuit-board feel)
    $circPen = New-Object System.Windows.Media.Pen((SB (CA 0x20 0x58 0xa6 0xff)), (1.5*$s))
    $circPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $circPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    # top-left
    $dc.DrawLine($circPen, (New-Object System.Windows.Point((22*$s),(40*$s))), (New-Object System.Windows.Point((22*$s),(20*$s))))
    $dc.DrawLine($circPen, (New-Object System.Windows.Point((22*$s),(20*$s))), (New-Object System.Windows.Point((50*$s),(20*$s))))
    # bottom-right
    $dc.DrawLine($circPen, (New-Object System.Windows.Point((234*$s),(216*$s))), (New-Object System.Windows.Point((234*$s),(236*$s))))
    $dc.DrawLine($circPen, (New-Object System.Windows.Point((234*$s),(236*$s))), (New-Object System.Windows.Point((206*$s),(236*$s))))
    # small dots at circuit ends
    $dotBrush = SB (CA 0x40 0x58 0xa6 0xff)
    $dc.DrawEllipse($dotBrush, $null, (New-Object System.Windows.Point((50*$s),(20*$s))), (2.5*$s), (2.5*$s))
    $dc.DrawEllipse($dotBrush, $null, (New-Object System.Windows.Point((22*$s),(40*$s))), (2.5*$s), (2.5*$s))
    $dc.DrawEllipse($dotBrush, $null, (New-Object System.Windows.Point((206*$s),(236*$s))), (2.5*$s), (2.5*$s))
    $dc.DrawEllipse($dotBrush, $null, (New-Object System.Windows.Point((234*$s),(216*$s))), (2.5*$s), (2.5*$s))

    # ── Glow behind floppy disk ──
    $glowBrush = New-Object System.Windows.Media.RadialGradientBrush
    $glowBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop((CA 0x28 0x58 0xa6 0xff), 0.0)))
    $glowBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop((CA 0x00 0x58 0xa6 0xff), 1.0)))
    $dc.DrawEllipse($glowBrush, $null, (New-Object System.Windows.Point((118*$s),(118*$s))), (90*$s), (90*$s))

    # ── Floppy disk body ──
    $diskFill = New-Object System.Windows.Media.LinearGradientBrush((CA 0x18 0x58 0xa6 0xff),(CA 0x0a 0x30 0x60 0xaa),45.0)
    $diskPen = New-Object System.Windows.Media.Pen((SB $cAccent), (4*$s))
    $diskPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $diskGeom = [System.Windows.Media.Geometry]::Parse([string]::Format("M{0},{1} L{2},{1} L{3},{4} L{3},{5} L{0},{5} Z", (68*$s),(52*$s),(172*$s),(188*$s),(68*$s),(196*$s)))
    $dc.DrawGeometry($diskFill, $diskPen, $diskGeom)

    # Label slot on top of disk
    $labelFill = SB (CA 0x30 0x58 0xa6 0xff)
    $labelPen2 = New-Object System.Windows.Media.Pen((SB $cAccent), (2*$s))
    $dc.DrawRoundedRectangle($labelFill, $labelPen2, (New-Object System.Windows.Rect((92*$s),(52*$s),(64*$s),(36*$s))), (3*$s), (3*$s))

    # Metal slider on label
    $sliderBrush = SB (CA 0x60 0x90 0xc0 0xff)
    $dc.DrawRoundedRectangle($sliderBrush, $null, (New-Object System.Windows.Rect((108*$s),(56*$s),(32*$s),(12*$s))), (2*$s), (2*$s))

    # Data window
    $dataFill = SB (CA 0x10 0x58 0xa6 0xff)
    $dataPen2 = New-Object System.Windows.Media.Pen((SB $cAccent), (2*$s))
    $dc.DrawRoundedRectangle($dataFill, $dataPen2, (New-Object System.Windows.Rect((84*$s),(120*$s),(88*$s),(56*$s))), (5*$s), (5*$s))

    # Data lines inside data window
    $linePen = New-Object System.Windows.Media.Pen((SB $cAccent), (2*$s))
    $linePen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $linePen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $dc.PushOpacity(0.8); $dc.DrawLine($linePen, (New-Object System.Windows.Point((96*$s),(136*$s))), (New-Object System.Windows.Point((140*$s),(136*$s)))); $dc.Pop()
    $dc.PushOpacity(0.55); $dc.DrawLine($linePen, (New-Object System.Windows.Point((96*$s),(148*$s))), (New-Object System.Windows.Point((156*$s),(148*$s)))); $dc.Pop()
    $dc.PushOpacity(0.35); $dc.DrawLine($linePen, (New-Object System.Windows.Point((96*$s),(160*$s))), (New-Object System.Windows.Point((124*$s),(160*$s)))); $dc.Pop()

    # ── Gear (green, bottom-right) ──
    $gearCx = 184*$s; $gearCy = 186*$s
    $gearCenter = New-Object System.Windows.Point($gearCx, $gearCy)

    # Outer glow for gear
    $gGlow = New-Object System.Windows.Media.RadialGradientBrush
    $gGlow.GradientStops.Add((New-Object System.Windows.Media.GradientStop((CA 0x30 0x3f 0xb9 0x50), 0.0)))
    $gGlow.GradientStops.Add((New-Object System.Windows.Media.GradientStop((CA 0x00 0x3f 0xb9 0x50), 1.0)))
    $dc.DrawEllipse($gGlow, $null, $gearCenter, (38*$s), (38*$s))

    # Gear teeth
    $toothBrush = SB $cGreen
    for ($a = 0; $a -lt 360; $a += 45) {
        $rad = $a * [Math]::PI / 180.0
        $tx = $gearCx + (26*$s) * [Math]::Cos($rad)
        $ty = $gearCy + (26*$s) * [Math]::Sin($rad)
        $dc.DrawEllipse($toothBrush, $null, (New-Object System.Windows.Point($tx, $ty)), (7*$s), (7*$s))
    }
    # Gear body
    $dc.DrawEllipse((SB $cGreen), $null, $gearCenter, (20*$s), (20*$s))
    # Gear center hole
    $dc.DrawEllipse((SB $cDark), (New-Object System.Windows.Media.Pen((SB $cGreenDk), (2*$s))), $gearCenter, (8*$s), (8*$s))

    # ── Tiny accent chevron (edit arrow) on gear ──
    $chevPen = New-Object System.Windows.Media.Pen((SB (C 0xff 0xff 0xff)), (2.5*$s))
    $chevPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $chevPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $chevPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $dc.DrawLine($chevPen, (New-Object System.Windows.Point(($gearCx - 5*$s),($gearCy))), (New-Object System.Windows.Point(($gearCx + 1*$s),($gearCy - 6*$s))))
    $dc.DrawLine($chevPen, (New-Object System.Windows.Point(($gearCx + 1*$s),($gearCy - 6*$s))), (New-Object System.Windows.Point(($gearCx + 7*$s),($gearCy))))

    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($sz, $sz, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    return $rtb
}

# ── Render the main logo with text (256x256) ──
function RenderLogoWithText {
    $sz = 256
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    # Background
    $bgRadial = New-Object System.Windows.Media.RadialGradientBrush
    $bgRadial.Center = (New-Object System.Windows.Point(0.35, 0.25))
    $bgRadial.GradientOrigin = (New-Object System.Windows.Point(0.35, 0.25))
    $bgRadial.RadiusX = 0.85; $bgRadial.RadiusY = 0.85
    $bgRadial.GradientStops.Add((New-Object System.Windows.Media.GradientStop((C 0x18 0x24 0x3a), 0.0)))
    $bgRadial.GradientStops.Add((New-Object System.Windows.Media.GradientStop($cBg1, 0.5)))
    $bgRadial.GradientStops.Add((New-Object System.Windows.Media.GradientStop($cBg2, 1.0)))
    $bPen = New-Object System.Windows.Media.Pen((SB $cBorder), 3.0)
    $dc.DrawRoundedRectangle($bgRadial, $bPen, (New-Object System.Windows.Rect(6,6,244,244)), 32, 32)

    # Circuit accent lines - top left
    $circPen = New-Object System.Windows.Media.Pen((SB (CA 0x25 0x58 0xa6 0xff)), 1.5)
    $circPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $circPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $dc.DrawLine($circPen, (New-Object System.Windows.Point(20,36)), (New-Object System.Windows.Point(20,16)))
    $dc.DrawLine($circPen, (New-Object System.Windows.Point(20,16)), (New-Object System.Windows.Point(48,16)))
    $dc.DrawLine($circPen, (New-Object System.Windows.Point(236,220)), (New-Object System.Windows.Point(236,240)))
    $dc.DrawLine($circPen, (New-Object System.Windows.Point(236,240)), (New-Object System.Windows.Point(208,240)))
    # dots
    $dotB = SB (CA 0x50 0x58 0xa6 0xff)
    $dc.DrawEllipse($dotB, $null, (New-Object System.Windows.Point(48,16)), 2.5, 2.5)
    $dc.DrawEllipse($dotB, $null, (New-Object System.Windows.Point(20,36)), 2.5, 2.5)
    $dc.DrawEllipse($dotB, $null, (New-Object System.Windows.Point(208,240)), 2.5, 2.5)
    $dc.DrawEllipse($dotB, $null, (New-Object System.Windows.Point(236,220)), 2.5, 2.5)

    # Glow behind icon area
    $glowB = New-Object System.Windows.Media.RadialGradientBrush
    $glowB.GradientStops.Add((New-Object System.Windows.Media.GradientStop((CA 0x28 0x58 0xa6 0xff), 0.0)))
    $glowB.GradientStops.Add((New-Object System.Windows.Media.GradientStop((CA 0x00 0x58 0xa6 0xff), 1.0)))
    $dc.DrawEllipse($glowB, $null, (New-Object System.Windows.Point(128, 90)), 80, 80)

    # ── Floppy disk (shifted up to make room for text) ──
    $oy = -28  # vertical offset to push disk up
    $diskFill = New-Object System.Windows.Media.LinearGradientBrush((CA 0x18 0x58 0xa6 0xff),(CA 0x0a 0x30 0x60 0xaa),45.0)
    $diskPen = New-Object System.Windows.Media.Pen((SB $cAccent), 4.0)
    $diskPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $diskGeom = [System.Windows.Media.Geometry]::Parse([string]::Format("M78,{0} L162,{0} L178,{1} L178,{2} L78,{2} Z", (52+$oy),(68+$oy),(160+$oy)))
    $dc.DrawGeometry($diskFill, $diskPen, $diskGeom)

    # Label slot
    $dc.DrawRoundedRectangle((SB (CA 0x30 0x58 0xa6 0xff)), (New-Object System.Windows.Media.Pen((SB $cAccent), 2)), (New-Object System.Windows.Rect(100,(52+$oy),56,32)), 3, 3)
    # Slider
    $dc.DrawRoundedRectangle((SB (CA 0x60 0x90 0xc0 0xff)), $null, (New-Object System.Windows.Rect(114,(56+$oy),28,10)), 2, 2)

    # Data window
    $dc.DrawRoundedRectangle((SB (CA 0x10 0x58 0xa6 0xff)), (New-Object System.Windows.Media.Pen((SB $cAccent), 2)), (New-Object System.Windows.Rect(92,(100+$oy),72,44)), 4, 4)
    $lp = New-Object System.Windows.Media.Pen((SB $cAccent), 2.0)
    $lp.StartLineCap = [System.Windows.Media.PenLineCap]::Round; $lp.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $dc.PushOpacity(0.8); $dc.DrawLine($lp, (New-Object System.Windows.Point(102,(112+$oy))), (New-Object System.Windows.Point(138,(112+$oy)))); $dc.Pop()
    $dc.PushOpacity(0.5); $dc.DrawLine($lp, (New-Object System.Windows.Point(102,(122+$oy))), (New-Object System.Windows.Point(150,(122+$oy)))); $dc.Pop()
    $dc.PushOpacity(0.3); $dc.DrawLine($lp, (New-Object System.Windows.Point(102,(132+$oy))), (New-Object System.Windows.Point(126,(132+$oy)))); $dc.Pop()

    # ── Gear ──
    $gcx = 172; $gcy = (148+$oy)
    $gc = New-Object System.Windows.Point($gcx, $gcy)
    $gGlow = New-Object System.Windows.Media.RadialGradientBrush
    $gGlow.GradientStops.Add((New-Object System.Windows.Media.GradientStop((CA 0x30 0x3f 0xb9 0x50), 0.0)))
    $gGlow.GradientStops.Add((New-Object System.Windows.Media.GradientStop((CA 0x00 0x3f 0xb9 0x50), 1.0)))
    $dc.DrawEllipse($gGlow, $null, $gc, 32, 32)
    for ($a = 0; $a -lt 360; $a += 45) {
        $rad = $a * [Math]::PI / 180.0
        $tx = $gcx + 22 * [Math]::Cos($rad); $ty = $gcy + 22 * [Math]::Sin($rad)
        $dc.DrawEllipse((SB $cGreen), $null, (New-Object System.Windows.Point($tx,$ty)), 6, 6)
    }
    $dc.DrawEllipse((SB $cGreen), $null, $gc, 17, 17)
    $dc.DrawEllipse((SB $cDark), (New-Object System.Windows.Media.Pen((SB $cGreenDk), 2)), $gc, 7, 7)
    # Edit chevron
    $chP = New-Object System.Windows.Media.Pen((SB (C 0xff 0xff 0xff)), 2.0)
    $chP.StartLineCap = [System.Windows.Media.PenLineCap]::Round; $chP.EndLineCap = [System.Windows.Media.PenLineCap]::Round; $chP.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $dc.DrawLine($chP, (New-Object System.Windows.Point(($gcx-4),($gcy))), (New-Object System.Windows.Point(($gcx+1),($gcy-5))))
    $dc.DrawLine($chP, (New-Object System.Windows.Point(($gcx+1),($gcy-5))), (New-Object System.Windows.Point(($gcx+6),($gcy))))

    # ── Horizontal divider line above text ──
    $divY = 156
    $divPen = New-Object System.Windows.Media.Pen((SB (CA 0x40 0x58 0xa6 0xff)), 1.0)
    $dc.DrawLine($divPen, (New-Object System.Windows.Point(40, $divY)), (New-Object System.Windows.Point(216, $divY)))
    # small diamond at center of divider
    $diaGeom = [System.Windows.Media.Geometry]::Parse([string]::Format("M128,{0} L132,{1} L128,{2} L124,{1} Z", ($divY-4),($divY),($divY+4)))
    $dc.DrawGeometry((SB (CA 0x60 0x58 0xa6 0xff)), $null, $diaGeom)

    # ── Text: "COI SAVE EDITOR" ──
    $tf1 = New-Object System.Windows.Media.Typeface("Segoe UI Semibold")
    $ft1 = New-Object System.Windows.Media.FormattedText("COI SAVE EDITOR", [System.Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $tf1, 24.0, (SB $cAccent), 96.0)
    $ft1.TextAlignment = [System.Windows.TextAlignment]::Center
    $dc.DrawText($ft1, (New-Object System.Windows.Point(128, 164)))

    # ── Text: "— ULTIMATE —" in green ──
    $tf2 = New-Object System.Windows.Media.Typeface("Segoe UI Bold")
    $ft2 = New-Object System.Windows.Media.FormattedText("ULTIMATE", [System.Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $tf2, 16.0, (SB $cGreen), 96.0)
    $ft2.TextAlignment = [System.Windows.TextAlignment]::Center
    $dc.DrawText($ft2, (New-Object System.Windows.Point(128, 194)))

    # Small dashes flanking ULTIMATE
    $dashPen = New-Object System.Windows.Media.Pen((SB (CA 0x60 0x3f 0xb9 0x50)), 1.5)
    $dashPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round; $dashPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $dc.DrawLine($dashPen, (New-Object System.Windows.Point(56, 205)), (New-Object System.Windows.Point(80, 205)))
    $dc.DrawLine($dashPen, (New-Object System.Windows.Point(176, 205)), (New-Object System.Windows.Point(200, 205)))

    # Bottom tagline
    $tf3 = New-Object System.Windows.Media.Typeface("Segoe UI")
    $ft3 = New-Object System.Windows.Media.FormattedText("Save Editor for Captain of Industry", [System.Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $tf3, 9.0, (SB (CA 0x80 0x8b 0x94 0x9e)), 96.0)
    $ft3.TextAlignment = [System.Windows.TextAlignment]::Center
    $dc.DrawText($ft3, (New-Object System.Windows.Point(128, 220)))

    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(256, 256, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    return $rtb
}

function ToPng([System.Windows.Media.Imaging.BitmapSource]$bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return $ms.ToArray()
}

# ── Generate icon frames (icon-only, no text for small sizes) ──
Write-Host "Rendering icon frames..."
$sizes = @(16, 32, 48, 256)
$pngList = New-Object System.Collections.ArrayList
foreach ($sz in $sizes) {
    Write-Host "  Creating ${sz}x${sz} icon frame..."
    $bytes = ToPng (RenderIcon $sz)
    [void]$pngList.Add($bytes)
}

# ── Save the main logo (with text) as logo256.png ──
Write-Host "Rendering main logo with text..."
$logoWithText = RenderLogoWithText
$pngPath = Join-Path $dir 'logo256.png'
[System.IO.File]::WriteAllBytes($pngPath, (ToPng $logoWithText))
Write-Host "  Saved logo256.png (with text)"

# ── Build ICO (uses icon-only frames) ──
Write-Host "Building ICO..."
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Length)
$dataOffset = 6 + ($sizes.Length * 16)
for ($i = 0; $i -lt $sizes.Length; $i++) {
    $w = if ($sizes[$i] -ge 256) { [byte]0 } else { [byte]$sizes[$i] }
    $bw.Write($w); $bw.Write($w); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $pngBytes = [byte[]]$pngList[$i]
    $bw.Write([uint32]$pngBytes.Length); $bw.Write([uint32]$dataOffset)
    $dataOffset += $pngBytes.Length
}
for ($i = 0; $i -lt $sizes.Length; $i++) { $bw.Write([byte[]]$pngList[$i]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
$bw.Close()
Write-Host "  Done! Icon saved to $outPath" -ForegroundColor Green
