@echo off
chcp 65001 > nul
REM ============================================================
REM  PaperSharp samples 一键跑
REM  双击即可：dotnet restore → dotnet run → 打开 output 目录
REM ============================================================
cd /d "%~dp0"

echo [1/3] dotnet restore...
dotnet restore || goto :fail

echo.
echo [2/3] dotnet run...
dotnet run -c Debug || goto :fail

echo.
echo [3/3] Opening output folder...
start "" explorer "%~dp0bin\Debug\net10.0\output"

goto :eof

:fail
echo.
echo Build failed. Check error above.
pause
