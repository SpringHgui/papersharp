# CiBench 一键运行（Windows PowerShell）
#   .\run.ps1                     全量：冷/热各 20 次 + 100/200/300 并发
#   .\run.ps1 --quick
#   .\run.ps1 --engines papersharp,puppeteer --repeats 5
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Rest
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'CiBench.csproj'
$dll = Join-Path $PSScriptRoot 'bin/Release/net10.0/CiBench.dll'

dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet $dll @Rest
exit $LASTEXITCODE
