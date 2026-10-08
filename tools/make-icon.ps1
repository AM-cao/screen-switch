# Generates app.ico (the exe / shortcut icon) with the same monitor drawing TrayIcons.cs uses (owned/blue variant).
# Sizes 16, 24, 32, 48, 64 as 32-bit BMP entries so the in-box csc.exe /win32icon accepts it.
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'app.ico'))
Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    $col = [System.Drawing.Color]::FromArgb(255, 47, 128, 237)
    $pen = New-Object System.Drawing.Pen $col, ([Math]::Max(1.5, 2.6 * $s)); $pen.LineJoin = 'Round'
    $x = 3 * $s; $y = 4 * $s; $w = 26 * $s; $h = 17 * $s; $r = 3 * $s
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $gp.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $gp.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $gp.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90); $gp.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $col), $gp)
    $g.DrawLine($pen, 16*$s, 21*$s, 16*$s, 26*$s)
    $g.DrawLine($pen, 9*$s, 27.5*$s, 23*$s, 27.5*$s)
    $white = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, 2.4 * $s))
    $white.LineJoin = 'Round'; $white.StartCap = 'Round'; $white.EndCap = 'Round'
    $g.DrawLine($white, 11*$s, 12.5*$s, 14.5*$s, 16*$s); $g.DrawLine($white, 14.5*$s, 16*$s, 21*$s, 9.5*$s)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64
$entries = @()
foreach ($sz in $sizes) {
    $bmp = Draw $sz
    $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
    # BITMAPINFOHEADER, height doubled (XOR + AND masks)
    $bw.Write([int32]40); $bw.Write([int32]$sz); $bw.Write([int32]($sz * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int32]0); $bw.Write([int32]($sz * $sz * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0)
    for ($y = $sz - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $sz; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([Math]::Ceiling($sz / 32.0) * 4)
    for ($y = 0; $y -lt $sz; $y++) { for ($i = 0; $i -lt $rowBytes; $i++) { $bw.Write([byte]0) } }
    $bw.Flush()
    $entries += ,@($sz, $ms.ToArray())
    $bmp.Dispose()
}

$fs = [IO.File]::Create($Out); $w = New-Object IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $sz = $e[0]; $data = $e[1]
    $w.Write([byte]$sz); $w.Write([byte]$sz); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int32]$data.Length); $w.Write([int32]$offset)
    $offset += $data.Length
}
foreach ($e in $entries) { $w.Write($e[1]) }
$w.Flush(); $fs.Close()
"wrote $Out ($((Get-Item $Out).Length) bytes)"
