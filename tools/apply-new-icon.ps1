# Regenerates all icon assets (NT.ico with 7 resolutions, extension icons, logo.png)
# from the master emblem at tools/icon-master.png. Fully self-contained.
[CmdletBinding()]
param()

Add-Type -AssemblyName System.Drawing

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$masterPath = Join-Path $repoRoot "tools\icon-master.png"

if (-not (Test-Path $masterPath)) {
    Write-Error "Master icon not found at $masterPath"
    exit 1
}

$masterBmp = New-Object System.Drawing.Bitmap($masterPath)

function Resize-Bitmap($src, $w, $h) {
    $dest = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dest)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $w, $h)
    $g.Dispose()
    return $dest
}

# 1. Update logo.png
Copy-Item $masterPath (Join-Path $repoRoot "docs\images\logo.png") -Force

# 2. Generate extension icons
$extDir = Join-Path $repoRoot "extension\icons"
if (-not (Test-Path $extDir)) { New-Item -ItemType Directory -Path $extDir | Out-Null }

$s128 = Resize-Bitmap $masterBmp 128 128
$s128.Save((Join-Path $extDir "128.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$s128.Dispose()

$s48 = Resize-Bitmap $masterBmp 48 48
$s48.Save((Join-Path $extDir "48.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$s48.Dispose()

$s16 = Resize-Bitmap $masterBmp 16 16
$s16.Save((Join-Path $extDir "16.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$s16.Dispose()

# 3. Build multi-resolution NT.ico (16, 24, 32, 48, 64, 128, 256)
$icoOut = Join-Path $repoRoot "src\NT.App\Assets\NT.ico"
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()

foreach ($s in $sizes) {
    $resized = Resize-Bitmap $masterBmp $s $s
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

$masterBmp.Dispose()

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

Write-Host "Successfully generated all icons from tools/icon-master.png!"
