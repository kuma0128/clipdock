$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$iconDir = Join-Path $root 'extension\icons'
New-Item -ItemType Directory -Path $iconDir -Force | Out-Null
foreach ($size in @(16, 32, 48, 128, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 128.0
    if ($size -eq 128) {
        $graphics.TranslateTransform(16, 16)
        $scale = 96 / 128.0
    }
    $graphics.ScaleTransform($scale, $scale)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(2, 2, 32, 32, 180, 90)
    $shape.AddArc(94, 2, 32, 32, 270, 90)
    $shape.AddArc(94, 94, 32, 32, 0, 90)
    $shape.AddArc(2, 94, 32, 32, 90, 90)
    $shape.CloseFigure()
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#A5E7BA'))
    $graphics.FillPath($brush, $shape)
    $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#142B1D'), 10)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($pen, 64, 29, 64, 78)
    $graphics.DrawLine($pen, 44, 60, 64, 80)
    $graphics.DrawLine($pen, 84, 60, 64, 80)
    $graphics.DrawLine($pen, 34, 99, 94, 99)
    $png = Join-Path $iconDir "$size.png"
    $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $pen.Dispose(); $brush.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$imageBytes = [System.IO.File]::ReadAllBytes((Join-Path $iconDir '256.png'))
$stream = [System.IO.File]::Create((Join-Path $root 'src\ClipDock.App\app.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$imageBytes.Length); $writer.Write([uint32]22)
$writer.Write($imageBytes); $writer.Dispose()
Write-Output 'Created ClipDock icons.'
