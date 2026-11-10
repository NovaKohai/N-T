# Draws the N&T icon and writes every icon asset: NT.ico, the extension icons, docs/images/logo.png
# and the 1024 px reference image tools/icon-master.png. Fully self-contained (System.Drawing only).
#
# The mark is original artwork: a flat teal tile with a download arrow whose shaft is split into two
# parallel bars of different length (one file arriving over several connections); the split runs a
# short way into the arrowhead, and the arrow lands on a baseline.
# It is drawn as vector shapes separately for each size, with coordinates snapped to whole pixels,
# so the 16 px frame is as crisp as the 256 px one instead of being a blurred downscale.
[CmdletBinding()]
param()

Add-Type -AssemblyName System.Drawing

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

$tileColor   = [System.Drawing.ColorTranslator]::FromHtml('#006C67')   # the app's Light-theme accent
$glyphColor  = [System.Drawing.Color]::White

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# Geometry is specified on a 64-unit grid and snapped to the pixel grid of the requested size.
function New-IconBitmap([int]$size) {
    $u = $size / 64.0
    function Snap([double]$v) { [int][Math]::Round($v * $u, [MidpointRounding]::AwayFromZero) }

    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile: full bleed at small sizes, a little breathing room from 48 px up.
    $inset = if ($size -ge 48) { Snap 2 } else { 0 }
    $radius = [Math]::Max(3, (Snap 14))
    $tile = New-RoundedRect $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) $radius
    $tileBrush = New-Object System.Drawing.SolidBrush($tileColor)
    $g.FillPath($tileBrush, $tile)

    $cx = $size / 2                                  # all sizes are even, so this is a pixel edge
    $bar = [Math]::Max(2, (Snap 7))                  # width of one shaft bar
    $gap = [Math]::Max(2, (Snap 5))                  # space between the two bars
    if (($gap % 2) -ne 0) { $gap += 1 }              # keep the pair centred on a pixel edge
    $leftX  = $cx - $gap / 2 - $bar
    $rightX = $cx + $gap / 2
    $topLeft  = Snap 12                              # first connection: started earlier, so it is longer
    $topRight = Snap 21
    $notch    = [Math]::Max(1, (Snap 6))
    $headTop  = Snap 31
    $headHalf = [Math]::Max(($gap / 2 + $bar + 2), (Snap 17))    # the head always overhangs the shaft by at least 2 px, or it stops reading as an arrow at 16 px
    $tip      = Snap 48
    $baseTop  = Snap 52
    $baseH    = [Math]::Max(1, (Snap 4))
    $baseHalf = Snap 15

    $white = New-Object System.Drawing.SolidBrush($glyphColor)
    $g.FillRectangle($white, [single]$leftX,  [single]$topLeft,  [single]$bar, [single]($headTop - $topLeft + 1))
    $g.FillRectangle($white, [single]$rightX, [single]$topRight, [single]$bar, [single]($headTop - $topRight + 1))
    $head = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([single]($cx - $headHalf), [single]$headTop)),
        (New-Object System.Drawing.PointF([single]($cx + $headHalf), [single]$headTop)),
        (New-Object System.Drawing.PointF([single]$cx, [single]$tip)))
    $g.FillPolygon($white, $head)
    # The split between the bars continues into the head as a notch, so the two connections read as one arrow.
    $g.FillRectangle($tileBrush, [single]($cx - $gap / 2), [single]$headTop, [single]$gap, [single]$notch)
    $g.FillRectangle($white, [single]($cx - $baseHalf), [single]$baseTop, [single](2 * $baseHalf), [single]$baseH)

    $white.Dispose(); $tileBrush.Dispose(); $tile.Dispose(); $g.Dispose()
    return $bmp
}

function Save-IconPng([int]$size, [string]$path) {
    $bmp = New-IconBitmap $size
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# 1. Reference image and README logo
Save-IconPng 1024 (Join-Path $repoRoot "tools\icon-master.png")
Save-IconPng 256 (Join-Path $repoRoot "docs\images\logo.png")

# 2. Extension icons
$extDir = Join-Path $repoRoot "extension\icons"
if (-not (Test-Path $extDir)) { New-Item -ItemType Directory -Path $extDir | Out-Null }
foreach ($s in 16, 48, 128) { Save-IconPng $s (Join-Path $extDir "$s.png") }

# 3. Build multi-resolution NT.ico (classic DIB frames up to 48 px, PNG frames above)
$icoOut = Join-Path $repoRoot "src\NT.App\Assets\NT.ico"
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = @()

foreach ($s in $sizes) {
    $resized = New-IconBitmap $s
    $pngMs = New-Object System.IO.MemoryStream
    $resized.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes = $pngMs.ToArray()

    if ($s -le 48) {
        # Build 32bpp DIB for classic Win32 compatibility
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)

        $bw.Write([uint32]40) # BITMAPINFOHEADER size
        $bw.Write([int32]$s)   # biWidth
        $bw.Write([int32]($s * 2)) # biHeight (XOR + AND mask)
        $bw.Write([uint16]1)  # biPlanes
        $bw.Write([uint16]32) # biBitCount
        $bw.Write([uint32]0)  # BI_RGB
        $bw.Write([uint32]($s * $s * 4)) # biSizeImage
        $bw.Write([int32]0)
        $bw.Write([int32]0)
        $bw.Write([uint32]0)
        $bw.Write([uint32]0)

        # Pixels bottom-to-top, BGRA
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $pixel = $resized.GetPixel($x, $y)
                $bw.Write([byte]$pixel.B)
                $bw.Write([byte]$pixel.G)
                $bw.Write([byte]$pixel.R)
                $bw.Write([byte]$pixel.A)
            }
        }

        # AND mask (1 bit per pixel, padded to 32 bits per row)
        $maskRowBytes = [int][Math]::Ceiling($s / 32.0) * 4
        $maskRow = New-Object byte[] $maskRowBytes
        for ($y = 0; $y -lt $s; $y++) {
            $bw.Write($maskRow, 0, $maskRowBytes)
        }

        $payload = $ms.ToArray()
        $bw.Dispose()
        $ms.Dispose()
    } else {
        # Vista+ PNG payload for larger sizes
        $payload = $pngBytes
    }

    $pngMs.Dispose()
    $resized.Dispose()

    $entries += @{
        Size = $s
        Payload = $payload
    }
}

# Pack ICONDIR
$icoMs = New-Object System.IO.MemoryStream
$icoBw = New-Object System.IO.BinaryWriter($icoMs)
$icoBw.Write([uint16]0) # Reserved
$icoBw.Write([uint16]1) # Resource type 1 = icon
$icoBw.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $sizeByte = if ($e.Size -ge 256) { [byte]0 } else { [byte]$e.Size }
    $icoBw.Write($sizeByte) # Width
    $icoBw.Write($sizeByte) # Height
    $icoBw.Write([byte]0)   # Colors
    $icoBw.Write([byte]0)   # Reserved
    $icoBw.Write([uint16]1) # Planes
    $icoBw.Write([uint16]32)# BitCount
    $icoBw.Write([uint32]$e.Payload.Length)
    $icoBw.Write([uint32]$offset)
    $offset += $e.Payload.Length
}

foreach ($e in $entries) {
    $icoBw.Write($e.Payload, 0, $e.Payload.Length)
}

[System.IO.File]::WriteAllBytes($icoOut, $icoMs.ToArray())
$icoBw.Dispose()
$icoMs.Dispose()

Write-Host "Drew the N&T icon: NT.ico ($($sizes -join ', ')), extension icons, docs logo and tools/icon-master.png"
