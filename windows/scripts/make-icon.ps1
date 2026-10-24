# Builds windows/src/SuYanShot/Resources/Glean.ico from the shared macOS icon (Resources/AppIcon.png).
# Each size is stored as a PNG frame, which Windows Vista and later read. Re-run after the icon changes:
#   powershell -ExecutionPolicy Bypass -File windows/scripts/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$source = Join-Path $root 'Resources\AppIcon.png'
$target = Join-Path $root 'windows\src\SuYanShot\Resources\Glean.ico'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$image = [System.Drawing.Image]::FromFile($source)
$frames = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($image, 0, 0, $size, $size)
    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}
$image.Dispose()

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 means 256
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)                     # no palette, reserved
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)                # planes, bits per pixel
    $writer.Write([UInt32]$frames[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
Write-Output "Wrote $target ($($out.Length) bytes, sizes $($sizes -join ', '))"
