@echo off
setlocal
rem Compiles the plugin with the C# compiler built into Windows (.NET Framework 4.x). No SDK needed.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find csc.exe - is .NET Framework 4.x installed?
  exit /b 1
)

set "OUT=%~dp0com.local.audiocontrols.sdPlugin\bin"
if not exist "%OUT%" mkdir "%OUT%"

"%CSC%" /nologo /target:winexe /optimize+ /out:"%OUT%\AudioControls.exe" /r:System.Web.Extensions.dll /r:System.Drawing.dll "%~dp0src\*.cs"
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo Built: %OUT%\AudioControls.exe
