$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$installerDirectory = $PSScriptRoot
$bannerPath = Join-Path $installerDirectory "InstallerBanner.bmp"
$dialogPath = Join-Path $installerDirectory "InstallerDialog.bmp"
$bannerPreviewPath = Join-Path $installerDirectory "InstallerBanner.png"
$dialogPreviewPath = Join-Path $installerDirectory "InstallerDialog.png"

function New-BrandBrush([int]$r, [int]$g, [int]$b) {
    return [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($r, $g, $b))
}

function Draw-NetworkMotif([System.Drawing.Graphics]$graphics, [int]$left, [int]$top, [int]$scale, [int]$alpha) {
    $lineColor = [System.Drawing.Color]::FromArgb($alpha, 87, 194, 203)
    $nodeColor = [System.Drawing.Color]::FromArgb($alpha, 142, 222, 213)
    $linePen = [System.Drawing.Pen]::new($lineColor, [Math]::Max(1, $scale))
    $nodeBrush = [System.Drawing.SolidBrush]::new($nodeColor)
    $points = @(
        [System.Drawing.Point]::new($left + 10 * $scale, $top + 76 * $scale),
        [System.Drawing.Point]::new($left + 61 * $scale, $top + 46 * $scale),
        [System.Drawing.Point]::new($left + 116 * $scale, $top + 68 * $scale),
        [System.Drawing.Point]::new($left + 166 * $scale, $top + 31 * $scale),
        [System.Drawing.Point]::new($left + 219 * $scale, $top + 58 * $scale)
    )
    try {
        for ($index = 0; $index -lt $points.Count - 1; $index++) {
            $graphics.DrawLine($linePen, $points[$index], $points[$index + 1])
        }
        $graphics.DrawLine($linePen, $points[1], [System.Drawing.Point]::new($left + 102 * $scale, $top + 112 * $scale))
        $graphics.DrawLine($linePen, [System.Drawing.Point]::new($left + 102 * $scale, $top + 112 * $scale), $points[2])
        foreach ($point in $points + @([System.Drawing.Point]::new($left + 102 * $scale, $top + 112 * $scale))) {
            $diameter = 8 * $scale
            $graphics.FillEllipse($nodeBrush, $point.X - $diameter / 2, $point.Y - $diameter / 2, $diameter, $diameter)
        }
    }
    finally {
        $linePen.Dispose()
        $nodeBrush.Dispose()
    }
}

function New-InstallerBanner {
    $bitmap = [System.Drawing.Bitmap]::new(493, 58, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, 493, 58),
        [System.Drawing.Color]::FromArgb(18, 38, 49),
        [System.Drawing.Color]::FromArgb(29, 62, 69),
        [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $accentBrush = New-BrandBrush 64 198 192
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.FillRectangle($background, 0, 0, 493, 58)
        $graphics.SetClip([System.Drawing.Rectangle]::new(330, 0, 163, 58))
        Draw-NetworkMotif $graphics 340 -50 1 110
        $graphics.ResetClip()
        $graphics.FillRectangle($accentBrush, 0, 0, 4, 58)
        $bitmap.Save($bannerPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
        $bitmap.Save($bannerPreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $accentBrush.Dispose()
        $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

function New-InstallerDialog {
    $bitmap = [System.Drawing.Bitmap]::new(493, 312, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $background = New-BrandBrush 245 248 247
    $panel = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, 493, 145),
        [System.Drawing.Color]::FromArgb(18, 38, 49),
        [System.Drawing.Color]::FromArgb(29, 62, 69),
        [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $titleFont = [System.Drawing.Font]::new("Segoe UI Semibold", 20, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $taglineFont = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $bodyFont = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $titleBrush = New-BrandBrush 249 251 250
    $taglineBrush = New-BrandBrush 183 213 212
    $bodyBrush = New-BrandBrush 40 58 64
    $accentBrush = New-BrandBrush 64 198 192
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.FillRectangle($background, 0, 0, 493, 312)
        $graphics.FillRectangle($panel, 0, 0, 493, 145)
        $graphics.FillRectangle($accentBrush, 0, 0, 7, 145)
        Draw-NetworkMotif $graphics 260 4 1 145
        $graphics.DrawString("NORTHSTAR", $titleFont, $titleBrush, 27, 30)
        $graphics.DrawString("NETWORK LAB", $titleFont, $titleBrush, 27, 58)
        $graphics.DrawString("Build  /  Simulate  /  Learn", $taglineFont, $taglineBrush, 29, 99)
        $graphics.DrawString("A focused workspace for educational network design", $bodyFont, $bodyBrush, 28, 176)
        $graphics.DrawString("and protocol simulation.", $bodyFont, $bodyBrush, 28, 197)
        $graphics.DrawString("Projects are stored locally unless you explicitly", $bodyFont, $bodyBrush, 28, 240)
        $graphics.DrawString("share a session with a peer.", $bodyFont, $bodyBrush, 28, 261)
        $bitmap.Save($dialogPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
        $bitmap.Save($dialogPreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $titleFont.Dispose(); $taglineFont.Dispose(); $bodyFont.Dispose()
        $titleBrush.Dispose(); $taglineBrush.Dispose(); $bodyBrush.Dispose(); $accentBrush.Dispose()
        $panel.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

New-InstallerBanner
New-InstallerDialog
Write-Host "Generated installer artwork: $bannerPath and $dialogPath"
Write-Host "PNG previews: $bannerPreviewPath and $dialogPreviewPath"