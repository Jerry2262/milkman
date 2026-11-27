@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "TEST_EXE=%TEMP%\Milkman-JsonRegression-%RANDOM%-%RANDOM%.exe"
if exist "%TEST_EXE%" exit /b 1
rem System.Web.Extensions is used only by the test oracle, not by the product sources.
"%CSC%" /nologo /codepage:65001 /target:exe /main:JsonRegression /optimize+ /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /out:"%TEST_EXE%" "%~dp0..\Layout.cs" "%~dp0..\Core.cs" "%~dp0..\Ui.cs" "%~dp0..\Form.cs" "%~dp0JsonRegression.cs"
if errorlevel 1 goto failed
"%TEST_EXE%"
set "RESULT=%ERRORLEVEL%"
goto cleanup
:failed
set "RESULT=1"
:cleanup
if exist "%TEST_EXE%" del /q "%TEST_EXE%"
exit /b %RESULT%
