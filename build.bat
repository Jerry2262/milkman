@echo off
rem Milkman 编译脚本(Windows 自带 csc.exe, 无需安装 SDK)
rem 源码位于 WSL: \\wsl.localhost\Ubuntu-24.04\home\jerry\milkman\Main.cs（改动 DISTRO 以适配其他环境）
set DISTRO=Ubuntu-24.04
set OUT=%LOCALAPPDATA%\Milkman
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

echo [1/1] 编译 Main.cs ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /out:"%OUT%\Milkman.exe" "\\wsl.localhost\%DISTRO%\home\jerry\milkman\Main.cs"
if errorlevel 1 (
  echo 编译失败
  pause
  exit /b 1
)
echo 编译成功: %OUT%\Milkman.exe
echo 启动: start "" "%OUT%\Milkman.exe"
pause
