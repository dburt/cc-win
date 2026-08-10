# Generates app.ico: the Claude spark on a dark rounded square, packed at every size
# Windows asks for (taskbar, Start, alt-tab, Explorer). Run from Windows PowerShell:
#   powershell.exe -ExecutionPolicy Bypass -File tools\make-icon.ps1 -Out app.ico
param([string]$Out = "app.ico")

Add-Type -AssemblyName System.Drawing

$Background = [System.Drawing.ColorTranslator]::FromHtml('#1B1B21')
$Spark      = [System.Drawing.ColorTranslator]::FromHtml('#D97757')
$Rays       = 11

function New-Frame([int]$Size) {
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded-square plate
    $radius = [Math]::Max(2, [int]($Size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $plate = New-Object System.Drawing.SolidBrush($Background)
    $g.FillPath($plate, $path)

    # Tapered rays: each is a triangle whose base width is measured perpendicular to the ray,
    # not as an angle at a tiny radius — otherwise the bases collapse to hairlines.
    $cx = $Size / 2.0
    $cy = $Size / 2.0
    $outer = $Size * 0.42
    $baseAt = $Size * 0.02
    $halfBase = $Size * 0.045
    $brush = New-Object System.Drawing.SolidBrush($Spark)

    for ($i = 0; $i -lt $Rays; $i++) {
        $a = ($i / [double]$Rays) * 2 * [Math]::PI - [Math]::PI / 2
        $ux = [Math]::Cos($a); $uy = [Math]::Sin($a)      # along the ray
        $px = -$uy;            $py = $ux                  # perpendicular to it
        $pts = @(
            (New-Object System.Drawing.PointF(($cx + $ux * $outer), ($cy + $uy * $outer))),
            (New-Object System.Drawing.PointF(($cx + $ux * $baseAt + $px * $halfBase), ($cy + $uy * $baseAt + $py * $halfBase))),
            (New-Object System.Drawing.PointF(($cx + $ux * $baseAt - $px * $halfBase), ($cy + $uy * $baseAt - $py * $halfBase)))
        )
        $g.FillPolygon($brush, [System.Drawing.PointF[]]$pts)
    }

    # small hub fills the seam where the ray bases meet
    $hub = $Size * 0.06
    $g.FillEllipse($brush, [float]($cx - $hub), [float]($cy - $hub), [float]($hub * 2), [float]($hub * 2))

    $brush.Dispose(); $plate.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

# Classic 32bpp DIB entries for the small sizes — GDI+ and older shell consumers do not
# reliably decode PNG-compressed entries. Only 256x256 is stored as PNG, which is both
# required by the format's size field and universally supported at that size.
function Get-DibEntry([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $w2 = New-Object System.IO.BinaryWriter($ms)

    $maskStride = [int](([Math]::Floor(($w + 31) / 32)) * 4)
    $w2.Write([uint32]40)                  # BITMAPINFOHEADER
    $w2.Write([int32]$w)
    $w2.Write([int32]($h * 2))             # height covers XOR + AND planes
    $w2.Write([uint16]1)
    $w2.Write([uint16]32)
    $w2.Write([uint32]0)                   # BI_RGB
    $w2.Write([uint32]($w * $h * 4 + $maskStride * $h))
    $w2.Write([int32]0); $w2.Write([int32]0)
    $w2.Write([uint32]0); $w2.Write([uint32]0)

    for ($y = $h - 1; $y -ge 0; $y--) {    # DIB rows run bottom-up
        $w2.Write($pixels, $y * $data.Stride, $w * 4)
    }
    $blank = New-Object byte[] $maskStride # alpha carries transparency; AND mask all opaque
    for ($y = 0; $y -lt $h; $y++) { $w2.Write($blank) }

    $w2.Flush()
    $bytes = $ms.ToArray()
    $w2.Dispose(); $ms.Dispose()
    return ,$bytes
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$payloads = @()

foreach ($s in $sizes) {
    $bmp = New-Frame $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $payloads += , @{ Size = $s; Bytes = $ms.ToArray() }
        $ms.Dispose()
    }
    else {
        $payloads += , @{ Size = $s; Bytes = (Get-DibEntry $bmp) }
    }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($fs)

$w.Write([uint16]0)                    # reserved
$w.Write([uint16]1)                    # type: icon
$w.Write([uint16]$payloads.Count)

$offset = 6 + (16 * $payloads.Count)
foreach ($p in $payloads) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $w.Write([byte]$dim)               # width  (0 means 256)
    $w.Write([byte]$dim)               # height
    $w.Write([byte]0)                  # palette size
    $w.Write([byte]0)                  # reserved
    $w.Write([uint16]1)                # colour planes
    $w.Write([uint16]32)               # bits per pixel
    $w.Write([uint32]([byte[]]$p.Bytes).Length)
    $w.Write([uint32]$offset)
    $offset += ([byte[]]$p.Bytes).Length
}
foreach ($p in $payloads) { $w.Write([byte[]]$p.Bytes) }

$w.Flush(); $w.Close(); $fs.Close()
foreach ($p in $payloads) { Write-Host "  $($p.Size)px -> $(([byte[]]$p.Bytes).Length) bytes" }
Write-Host "wrote $Out ($($payloads.Count) sizes, $((Get-Item $Out).Length) bytes)"
