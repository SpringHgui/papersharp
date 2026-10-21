param([Parameter(Mandatory=$true)][string]$Path,
      [int]$X = 0, [int]$Y = 0, [int]$W = 900, [int]$H = 260,
      [string]$Out = "")
# 从 PNG 里裁一块放大 2 倍存盘，肉眼核对重影用。缺省输出 <path>-crop.png
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Path)
if ($W -le 0 -or $X + $W -gt $bmp.Width) { $W = $bmp.Width - $X }
if ($H -le 0 -or $Y + $H -gt $bmp.Height) { $H = $bmp.Height - $Y }
$rect = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
$crop = $bmp.Clone($rect, $bmp.PixelFormat)
$big = New-Object System.Drawing.Bitmap($W * 2, $H * 2)
$g = [System.Drawing.Graphics]::FromImage($big)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.DrawImage($crop, 0, 0, $W * 2, $H * 2)
$g.Dispose()
if ($Out -eq "") { $Out = "$Path-crop.png" }
$big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "$Out ($($W)x$H @ $X,$Y)"
