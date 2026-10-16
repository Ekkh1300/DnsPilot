# Generates DnsPilot\Assets\dns-pilot.ico (and docs\dns-pilot-icon.png for the README).
# ASCII only (PS 5.1 reads BOM-less files as ANSI).
#
#   powershell -File tools\New-Icon.ps1
#
# The icon is drawn from geometry in this file rather than committed as opaque artwork, so a
# brand-colour change is one edit and a rebuild, not a hunt for a source file that may not exist.
#
# Design: an indigo squircle - the two brand indigos, #4F5BD5 -> #7B85F0, running top-left to
# bottom-right - carrying a white navigation cursor. The cursor is the "Pilot" of DnsPilot: it
# points the way, which is what choosing a resolver does. It has no lock, shield or tunnel in it,
# because this app changes which server is asked, and an icon that implied otherwise would be
# saying something the program does not do.
#
# Two implementation notes that are the whole reason this file is longer than the drawing:
#
#   1. Frames are rendered into a 32bpp *premultiplied* surface. GDI+ blends against the
#      backdrop in premultiplied space, so on an ordinary Argb surface every anti-aliased edge
#      would bake the transparent-black backdrop into the pixel (edge RGB comes out ~half the
#      real colour at 50% coverage) and the icon would wear a dark halo on a light desktop.
#      The frames are un-premultiplied on the way out because an ICO frame stores straight,
#      unassociated alpha. Draw premultiplied, ship straight.
#
#   2. Sizes are drawn independently at their own size rather than downscaled from one master.
#      A cursor that reads cleanly at 256 has strokes too thin to survive at 16; every icon
#      tool solves that by letting each frame be drawn for its own pixel grid.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root   = Split-Path -Parent $PSScriptRoot
$icoOut = Join-Path $root 'DnsPilot\Assets\dns-pilot.ico'
$pngOut = Join-Path $root 'docs\dns-pilot-icon.png'

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $s, $s, `
        ([System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode  = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # Squircle: four quarter-circle corners, radius 22% of the side.
        $r = [int]([math]::Round($s * 0.22))
        $d = $r * 2
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($s - $d, 0, $d, $d, 270, 90)
        $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
        $path.AddArc(0, $s - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $bounds = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $s, $s
        $c1 = [System.Drawing.Color]::FromArgb(255, 0x4F, 0x5B, 0xD5)
        $c2 = [System.Drawing.Color]::FromArgb(255, 0x7B, 0x85, 0xF0)
        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $bounds, $c1, $c2, 45.0
        $g.FillPath($grad, $path)
        $grad.Dispose()
        $path.Dispose()

        # Navigation cursor: tip, right wing, tail notch, left wing.
        $white = New-Object System.Drawing.SolidBrush -ArgumentList `
            ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
        $pts = @(
            (New-Object System.Drawing.PointF -ArgumentList (0.500 * $s), (0.155 * $s)),
            (New-Object System.Drawing.PointF -ArgumentList (0.845 * $s), (0.845 * $s)),
            (New-Object System.Drawing.PointF -ArgumentList (0.500 * $s), (0.645 * $s)),
            (New-Object System.Drawing.PointF -ArgumentList (0.155 * $s), (0.845 * $s))
        )
        $g.FillPolygon($white, $pts)
        $white.Dispose()
    }
    finally {
        $g.Dispose()
    }
    return $bmp
}

# PArgb pixels -> straight-alpha BGRA, which is what an ICO frame stores.
function Get-StraightBgra([System.Drawing.Bitmap]$bmp) {
    $rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $bmp.Width, $bmp.Height
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    try {
        $len = $data.Stride * $bmp.Height
        $buf = New-Object byte[] -ArgumentList $len
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $len)
    }
    finally {
        $bmp.UnlockBits($data)
    }
    for ($i = 0; $i -lt $len; $i += 4) {
        $a = [int]$buf[$i + 3]
        if ($a -eq 0) {
            $buf[$i] = 0; $buf[$i + 1] = 0; $buf[$i + 2] = 0
        }
        elseif ($a -lt 255) {
            $buf[$i]     = [Math]::Min(255, [int](($buf[$i]     * 255 + ($a / 2)) / $a))
            $buf[$i + 1] = [Math]::Min(255, [int](($buf[$i + 1] * 255 + ($a / 2)) / $a))
            $buf[$i + 2] = [Math]::Min(255, [int](($buf[$i + 2] * 255 + ($a / 2)) / $a))
        }
    }
    return , $buf
}

# BITMAPINFOHEADER + bottom-up BGRA + 1-bit AND mask (1 where fully transparent, so colour-only
# renderers still cut the shape out instead of showing a filled square).
function New-BmpFrame([byte[]]$bgra, [int]$s) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter -ArgumentList $ms
    $maskStride = [int]([math]::Ceiling($s / 32.0)) * 4

    $bw.Write([uint32]40)                    # biSize
    $bw.Write([int]$s)                       # biWidth
    $bw.Write([int]($s * 2))                 # biHeight: XOR *and* AND stacked
    $bw.Write([uint16]1)                     # biPlanes
    $bw.Write([uint16]32)                    # biBitCount
    $bw.Write([uint32]0)                     # biCompression = BI_RGB
    $bw.Write([uint32]($s * $s * 4))         # biSizeImage
    $bw.Write([int]0); $bw.Write([int]0)     # biXPelsPerMeter, biYPelsPerMeter
    $bw.Write([uint32]0); $bw.Write([uint32]0)  # biClrUsed, biClrImportant

    for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($bgra, $y * $s * 4, $s * 4) }

    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] -ArgumentList $maskStride
        for ($x = 0; $x -lt $s; $x++) {
            if ($bgra[($y * $s + $x) * 4 + 3] -eq 0) {
                $b = $x -shr 3
                $row[$b] = $row[$b] -bor (0x80 -shr ($x -band 7))
            }
        }
        $bw.Write($row)
    }

    $bw.Flush()
    # The comma matters: a bare byte[] returned from a function is enumerated into the pipeline
    # and re-collected as Object[], which silently breaks every length and offset below.
    return , $ms.ToArray()
}

function New-PngFrame([byte[]]$bgra, [int]$s) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $s, $s, `
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $s, $s
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        [System.Runtime.InteropServices.Marshal]::Copy($bgra, 0, $data.Scan0, $bgra.Length)
    }
    finally {
        $bmp.UnlockBits($data)
    }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $icoOut) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pngOut) | Out-Null

$frames = @()
# 16-128 as BMP frames, 256 as PNG: the standard modern layout. A 256 BMP frame alone would
# be a quarter of a megabyte of uncompressed pixels inside the exe for no benefit - every
# shell surface from Vista on decodes a PNG frame.
foreach ($s in @(16, 24, 32, 48, 64, 128)) {
    $bmp = New-Frame $s
    try     { $frames += , @{ Size = $s; Data = (New-BmpFrame (Get-StraightBgra $bmp) $s) } }
    finally { $bmp.Dispose() }
}

# The same 256 frame ships twice: inside the .ico, and as a plain PNG the READMEs point at.
$bmp = New-Frame 256
try {
    $bgra    = Get-StraightBgra $bmp
    $pngData = New-PngFrame $bgra 256
    $frames += , @{ Size = 256; Data = $pngData }
    [System.IO.File]::WriteAllBytes($pngOut, $pngData)
}
finally {
    $bmp.Dispose()
}

# Write the .ico: ICONDIR, one ICONDIRENTRY per frame, then the frame data back to back.
$fs = [System.IO.File]::Create($icoOut)
try {
    $bw = New-Object System.IO.BinaryWriter -ArgumentList $fs
    $bw.Write([uint16]0)                       # reserved
    $bw.Write([uint16]1)                       # type: 1 = icon
    $bw.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    foreach ($f in $frames) {
        $bw.Write([byte]($f.Size % 256))       # width;  0 means 256
        $bw.Write([byte]($f.Size % 256))       # height; 0 means 256
        $bw.Write([byte]0)                     # colour count: 32bpp, unused
        $bw.Write([byte]0)                     # reserved
        $bw.Write([uint16]1)                   # planes
        $bw.Write([uint16]32)                  # bit count
        $bw.Write([uint32]$f.Data.Length)
        $bw.Write([uint32]$offset)
        $offset += $f.Data.Length
    }
    foreach ($f in $frames) { $bw.Write($f.Data) }
    $bw.Flush()
}
finally {
    $fs.Dispose()
}

Write-Output ("OK  {0}  ({1} bytes, {2} frames)" -f $icoOut, (Get-Item $icoOut).Length, $frames.Count)
