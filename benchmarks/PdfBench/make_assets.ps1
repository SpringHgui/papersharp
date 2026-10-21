Add-Type -AssemblyName System.Drawing
$out = $PSScriptRoot + '\spec\assets'

# photo.jpg — 400x267 opaque "photo"-like: sky gradient, sun, mountains, ground
$bmp = New-Object System.Drawing.Bitmap(400, 267, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$rect = New-Object System.Drawing.Rectangle(0, 0, 400, 267)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255,135,206,235), [System.Drawing.Color]::FromArgb(255,255,200,120), 45)
$g.FillRectangle($brush, $rect)
$g.FillEllipse([System.Drawing.Brushes]::Gold, 300, 30, 60, 60)
$m1 = @(
  (New-Object System.Drawing.Point(0,267)),
  (New-Object System.Drawing.Point(120,110)),
  (New-Object System.Drawing.Point(240,267)))
$g.FillPolygon((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,70,110,90))), $m1)
$m2 = @(
  (New-Object System.Drawing.Point(140,267)),
  (New-Object System.Drawing.Point(280,80)),
  (New-Object System.Drawing.Point(400,267)))
$g.FillPolygon((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,50,90,75))), $m2)
$g.FillRectangle([System.Drawing.Brushes]::DarkOliveGreen, 0, 235, 400, 32)
$g.Dispose()
$bmp.Save($out + '\photo.jpg', [System.Drawing.Imaging.ImageFormat]::Jpeg)
$bmp.Dispose()

# logo-alpha.png — 240x120, transparent bg, translucent disc + solid rounded square + text
$bmp2 = New-Object System.Drawing.Bitmap(240, 120, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
$g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g2.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
$bAlpha = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(120, 231, 76, 60))
$g2.FillEllipse($bAlpha, 8, 8, 104, 104)
$bSolid = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 52, 73, 94))
$g2.FillRectangle($bSolid, 96, 34, 60, 60)
$g2.DrawEllipse([System.Drawing.Pens]::White, 40, 40, 40, 40)
$g2.DrawString('ALPHA', (New-Object System.Drawing.Font('Arial', 14, [System.Drawing.FontStyle]::Bold)), [System.Drawing.Brushes]::White, 158, 50)
$g2.Dispose()
$bmp2.Save($out + '\logo-alpha.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp2.Dispose()
Write-Output 'assets OK'
