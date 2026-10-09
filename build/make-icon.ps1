param([string]$IcoPath, [string]$PreviewDir)
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Teal rounded square, slightly lighter at the top.
    $r = 56; $x = 8; $y = 8; $w = 240
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $path.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $path.AddArc($x+$w-2*$r, $y+$w-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc($x, $y+$w-2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 8)), ([System.Drawing.Point]::new(0, 248)), ([System.Drawing.Color]::FromArgb(20, 184, 166)), ([System.Drawing.Color]::FromArgb(15, 118, 110))
    $g.FillPath($bg, $path)
    # The waves make the glyph right-heavy; nudge it left so the margins match.
    $g.TranslateTransform(-8, 0)

    # Speaker: body plus cone.
    $white = [System.Drawing.Brushes]::White
    $g.FillRectangle($white, 46, 100, 40, 56)
    $cone = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(84, 100), [System.Drawing.PointF]::new(134, 58),
        [System.Drawing.PointF]::new(134, 198), [System.Drawing.PointF]::new(84, 156))
    $g.FillPolygon($white, $cone)

    # Sound waves; one thicker wave at tray sizes so it stays legible.
    if ($size -le 20) { $waves = @(@(52, 26)) } else { $waves = @(@(44, 18), @(82, 18)) }
    foreach ($wave in $waves) {
        $rad = $wave[0]
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $wave[1]
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, 134 - $rad, 128 - $rad, 2*$rad, 2*$rad, -48, 96)
        $pen.Dispose()
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewDir -and ($s -in 16, 32, 256)) { $bmp.Save((Join-Path $PreviewDir "kvmate-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed frames (supported since Windows Vista).
$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[IO.File]::WriteAllBytes($IcoPath, $out.ToArray())
"wrote $IcoPath ($($out.Length) bytes)"
