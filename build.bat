@echo off
setlocal
rem Milkman build script - uses the csc.exe bundled with Windows, no SDK needed.
rem The source path is taken from this script's directory.
set "OUT=%LOCALAPPDATA%\Milkman"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%OUT%" mkdir "%OUT%"

echo [1/1] Compiling Main.cs ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /out:"%OUT%\Milkman.exe" "%~dp0Main.cs"
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Build OK: %OUT%\Milkman.exe
echo Run: start "" "%OUT%\Milkman.exe"
pause
