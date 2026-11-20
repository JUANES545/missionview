[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#7DDB9B'))
    $memory = [IO.MemoryStream]::new()
    try {
        $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#202522'))
        foreach ($pane in @(@(2, 3, 10, 21), @(13, 3, 22, 10), @(13, 13, 22, 21))) {
            # Snap each edge to the pixel grid for the small tray sizes.
            $left = [int][Math]::Round($pane[0] * $size / 24)
            $top = [int][Math]::Round($pane[1] * $size / 24)
            $right = [int][Math]::Round($pane[2] * $size / 24)
            $bottom = [int][Math]::Round($pane[3] * $size / 24)
            $graphics.FillRectangle($brush, $left, $top, ($right - $left), ($bottom - $top))
        }
        $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
        [pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() }
    } finally {
        $memory.Dispose()
        $brush.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    $writer.Flush()
    [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'MissionView.ico'), $stream.ToArray())
} finally {
    $writer.Dispose()
    $stream.Dispose()
}
