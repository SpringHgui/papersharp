param([string]$Path, [int]$YMax = 300, [int]$Tol = 1)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap($Path)
Write-Host "$Path $($bmp.Width)x$($bmp.Height)"
for ($y = 0; $y -lt [Math]::Min($YMax, $bmp.Height); $y++) {
  $n = 0; $x0 = -1; $x1 = -1
  for ($x = 0; $x -lt $bmp.Width; $x++) {
    $c = $bmp.GetPixel($x, $y)
    if ([Math]::Abs($c.R - 240) -le $Tol -and [Math]::Abs($c.G - 240) -le $Tol -and [Math]::Abs($c.B - 240) -le $Tol) {
      $n++; if ($x0 -lt 0) { $x0 = $x }; $x1 = $x
    }
  }
  if ($n -gt 0) { Write-Host ("row {0,4} n={1,5} x=[{2},{3}]" -f $y, $n, $x0, $x1) }
}
$bmp.Dispose()
