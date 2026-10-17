param([string]$pdf,[string]$prefix,[double]$scale=2.0)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Data.Pdf.PdfDocument,Windows.Data.Pdf,ContentType=WindowsRuntime]
[void][Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
[void][Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
$ext=[System.WindowsRuntimeSystemExtensions]
$asTaskOp=$ext.GetMethods()|?{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'}|select -First 1
$asTaskAct=$ext.GetMethods()|?{$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'}|select -First 1
function AwaitOp($op,$t){$m=$asTaskOp.MakeGenericMethod($t);$tk=$m.Invoke($null,@($op));$tk.Wait();$tk.Result}
function AwaitAct($a){$tk=$asTaskAct.Invoke($null,@($a));$tk.Wait()}
$streamType=[Windows.Storage.Streams.InMemoryRandomAccessStream]
$file=AwaitOp ([Windows.Storage.StorageFile]::GetFileFromPathAsync($pdf)) ([Windows.Storage.StorageFile])
$pdfdoc=AwaitOp ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
$n=$pdfdoc.PageCount
Write-Output ("pages="+$n)
for($i=0;$i -lt $n;$i++){
  $page=$pdfdoc.GetPage([uint32]$i)
  $out=New-Object $streamType
  AwaitAct ($page.RenderToStreamAsync($out,$null))
  $rs=[System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($out.GetInputStreamAt(0))
  $ms=New-Object System.IO.MemoryStream; $rs.CopyTo($ms)
  $p="{0}_p{1}.png" -f $prefix,($i+1)
  [System.IO.File]::WriteAllBytes($p,$ms.ToArray())
  Write-Output ("wrote "+$p+" "+$ms.Length)
}
