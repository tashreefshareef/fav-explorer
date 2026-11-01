# Generates the two icons Fav Explorer needs.
#
#   assets\heart.ico  the overlay badge: a red heart on a white disc, because it
#                     gets composited onto photographs whose colours we can't know
#   assets\app.ico    the tray and window icon: just the heart
#
# System.Drawing can't write a multi-resolution .ico, so we render each size and
# assemble the container by hand. Entries are 32bpp DIBs rather than embedded PNGs:
# both work on Windows 11, but DIB is what every icon consumer has always accepted.

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# Every one of these has to exist, because Windows does not draw a frame at its
# native size — it takes whichever frame is nearest the size it wants and *scales*
# it. Stopping at 64px meant a 256px thumbnail scaled the 64px frame up 4x and the
# badge came out four times too big. Frames all the way to 256 mean no upscaling,
# so the 10px heart drawn into each one stays 10px on screen.
#
# The large frames are PNG-compressed inside the .ico (see Write-Ico). A 256px DIB
# would add 256 KB to a DLL that lives in every Explorer process; as PNG the same
# mostly-transparent frame is about a kilobyte.
$BADGE_SIZES = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
# The app icon is a normal window/tray icon and does get shown large.
$APP_SIZES   = @(16, 20, 24, 32, 48, 64, 128, 256)

# Heart outline as four cubic Beziers on a 0..1 square, y pointing down.
# Point 0 is the notch between the lobes; the path runs clockwise from there.
$HEART = @(
    @(0.50, 0.27),
    @(0.61, 0.08), @(0.97, 0.11), @(0.96, 0.38),
    @(0.95, 0.63), @(0.63, 0.79), @(0.50, 0.96),
    @(0.37, 0.79), @(0.05, 0.63), @(0.04, 0.38),
    @(0.03, 0.11), @(0.39, 0.08), @(0.50, 0.27)
)

function New-HeartPath {
    param([double]$x, [double]$y, [double]$w, [double]$h)
    $pts = New-Object 'System.Drawing.PointF[]' $HEART.Count
    for ($i = 0; $i -lt $HEART.Count; $i++) {
        $pts[$i] = New-Object System.Drawing.PointF(
            [float]($x + $HEART[$i][0] * $w),
            [float]($y + $HEART[$i][1] * $h))
    }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddBeziers($pts)
    $path.CloseFigure()
    return $path
}

function New-Frame {
    param([int]$size, [bool]$withDisc)

    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $red = [System.Drawing.Color]::FromArgb(255, 214, 36, 76)

    if ($withDisc) {
        # These numbers are measured, not invented. They are what OneDrive's own
        # overlay badges do, read straight out of FileSyncShell64.dll with
        # PrivateExtractIcons.
        #
        # The lesson in them: a badge should be neither a constant fraction of the
        # frame (huge on a 256px thumbnail) nor a constant pixel size (invisible on
        # one). OneDrive grows it *sub-linearly* — roughly frame^0.7 — so it goes from
        # 9px on a 16px row icon to 48px on a 256px thumbnail, staying legible at both
        # ends without ever swamping the picture. Google Drive, for comparison, holds
        # a flat ~47% and is correspondingly heavy-handed on large thumbnails.
        #
        # FAVEXPLORER_BADGE_BOOST scales the whole curve if you want it louder or
        # quieter than OneDrive.
        $oneDrive = @{ 16=9; 20=9; 24=13; 32=15; 40=15; 48=16; 64=16; 96=24; 128=25; 256=48 }
        $base = if ($oneDrive.ContainsKey($size)) { [double]$oneDrive[$size] }
                else { [Math]::Round([Math]::Pow($size, 0.7)) }
        $boost = if ($env:FAVEXPLORER_BADGE_BOOST) { [double]$env:FAVEXPLORER_BADGE_BOOST } else { 1.0 }
        $d = [Math]::Min(($base * $boost), $size - 2.0)
        $padX = 1.0
        $padY = $size - $d - 1.0
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
        $g.FillEllipse($white, [float]$padX, [float]$padY, [float]$d, [float]$d)

        # A faint grey ring keeps the white disc from vanishing into a white photo.
        $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 30, 25, 28)), ([float]([Math]::Max(1.0, $d * 0.045)))
        $g.DrawEllipse($ring, [float]$padX, [float]$padY, [float]$d, [float]$d)
        $ring.Dispose(); $white.Dispose()

        # OneDrive's glyph fills more of its circle than a 56% heart does, and the
        # heart is the part that has to be recognisable at 9px, so give it more room.
        $hw = $d * 0.64
        $hh = $hw * 0.92
        $hx = $padX + ($d - $hw) / 2.0
        $hy = $padY + ($d - $hh) / 2.0 + $d * 0.015
        $path = New-HeartPath $hx $hy $hw $hh
    }
    else {
        $hw = $size * 0.90
        $hh = $hw * 0.92
        $hx = ($size - $hw) / 2.0
        $hy = ($size - $hh) / 2.0
        $path = New-HeartPath $hx $hy $hw $hh
    }

    $brush = New-Object System.Drawing.SolidBrush $red
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function Get-DibEntry {
    param([System.Drawing.Bitmap]$bmp)

    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # BITMAPINFOHEADER. Height is doubled because the DIB nominally carries the
    # colour bitmap stacked on its AND mask.
    $bw.Write([uint32]40)
    $bw.Write([int32]$s)
    $bw.Write([int32]($s * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)            # BI_RGB
    $bw.Write([uint32]($s * $s * 4))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # Colour data, bottom-up.
    for ($y = $s - 1; $y -ge 0; $y--) {
        $bw.Write($raw, $y * $stride, $s * 4)
    }

    # AND mask: all zeros. The 32bpp alpha channel is what actually does the
    # masking on anything newer than Windows 2000, but the bytes must be present
    # or the entry is malformed.
    $maskRow = [int]([Math]::Floor(($s + 31) / 32) * 4)
    $zeros = New-Object byte[] ($maskRow * $s)
    $bw.Write($zeros, 0, $zeros.Length)

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

# Vista and later accept a whole PNG file as an icon entry. For the big frames that
# are almost entirely transparent this is the difference between a kilobyte and a
# quarter of a megabyte.
function Get-PngEntry {
    param([System.Drawing.Bitmap]$bmp)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

function Write-Ico {
    param([string]$path, [bool]$withDisc, [int[]]$sizes)

    $frames = @()
    foreach ($s in $sizes) {
        $bmp = New-Frame -size $s -withDisc $withDisc
        # DIB below 96px, where the widest range of consumers has always accepted it;
        # PNG above, where the size saving actually matters.
        $bytes = if ($s -ge 96) { Get-PngEntry $bmp } else { Get-DibEntry $bmp }
        $frames += , @{ Size = $s; Bytes = $bytes }
        $bmp.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    $bw.Write([uint16]0)                 # reserved
    $bw.Write([uint16]1)                 # type: icon
    $bw.Write([uint16]$frames.Count)

    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = $f.Size
        if ($dim -ge 256) { $dim = 0 }   # 256 is encoded as 0
        $bw.Write([byte]$dim)            # width
        $bw.Write([byte]$dim)            # height
        $bw.Write([byte]0)               # palette entries
        $bw.Write([byte]0)               # reserved
        $bw.Write([uint16]1)             # planes
        $bw.Write([uint16]32)            # bits per pixel
        $bw.Write([uint32]$f.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $f.Bytes.Length
    }
    foreach ($f in $frames) { $bw.Write($f.Bytes, 0, $f.Bytes.Length) }

    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()

    Write-Output ("{0}  {1:N0} bytes, {2} sizes" -f (Split-Path -Leaf $path), (Get-Item $path).Length, $frames.Count)
}

Write-Ico -path (Join-Path $assets 'heart.ico') -withDisc $true  -sizes $BADGE_SIZES
Write-Ico -path (Join-Path $assets 'app.ico')   -withDisc $false -sizes $APP_SIZES

# The resource compiler reads heart.ico from beside overlay.rc.
Copy-Item (Join-Path $assets 'heart.ico') (Join-Path $root 'src\overlay\heart.ico') -Force
Write-Output "copied heart.ico -> src\overlay\"
