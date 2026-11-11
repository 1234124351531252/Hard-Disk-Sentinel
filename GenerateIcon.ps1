param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$folder = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()

foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 256.0
    $graphics.ScaleTransform([single]$scale, [single]$scale)

    $background = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $background.AddArc(18, 18, 56, 56, 180, 90)
    $background.AddArc(182, 18, 56, 56, 270, 90)
    $background.AddArc(182, 182, 56, 56, 0, 90)
    $background.AddArc(18, 182, 56, 56, 90, 90)
    $background.CloseFigure()
    $graphics.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 20, 34, 59)), $background)

    $diskBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 246, 248, 253))
    $diskPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diskPath.AddArc(48, 65, 28, 28, 180, 90)
    $diskPath.AddArc(180, 65, 28, 28, 270, 90)
    $diskPath.AddArc(180, 161, 28, 28, 0, 90)
    $diskPath.AddArc(48, 161, 28, 28, 90, 90)
    $diskPath.CloseFigure()
    $graphics.FillPath($diskBrush, $diskPath)

    $bluePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 76, 112, 246), 13)
    $bluePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $bluePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawArc($bluePen, 82, 86, 92, 92, 205, 250)

    $greenBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 32, 166, 122))
    $graphics.FillEllipse($greenBrush, 115, 115, 26, 26)
    $graphics.FillRectangle($greenBrush, 64, 190, 128, 9)

    $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $graphics.FillEllipse($whiteBrush, 73, 191, 7, 7)
    $graphics.FillEllipse($whiteBrush, 184, 191, 7, 7)

    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($stream.ToArray())
    $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    $background.Dispose(); $diskPath.Dispose(); $diskBrush.Dispose(); $bluePen.Dispose(); $greenBrush.Dispose(); $whiteBrush.Dispose()
}

$directoryBytes = 6 + (16 * $sizes.Count)
$offset = $directoryBytes
$file = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $frame = $frames[$i]
        $writer.Write([byte]$(if ($s -eq 256) { 0 } else { $s }))
        $writer.Write([byte]$(if ($s -eq 256) { 0 } else { $s }))
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
}
finally { $writer.Dispose(); $file.Dispose() }
