@echo off
setlocal
rem Milkman build script - uses the csc.exe bundled with Windows, no SDK needed.
rem Source files are taken from this script's directory; still one self-contained EXE.
set "OUT=%LOCALAPPDATA%\Milkman"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%OUT%" mkdir "%OUT%"

echo [1/1] Compiling ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:"%~dp0app.ico" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:"%OUT%\Milkman.exe" "%~dp0Layout.cs" "%~dp0Core.cs" "%~dp0Ui.cs" "%~dp0Form.cs"
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Build OK: %OUT%\Milkman.exe
echo Run: start "" "%OUT%\Milkman.exe"
pause
