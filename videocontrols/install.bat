@echo off
setlocal
rem Close the AJAZZ Stream Dock app before running this (it locks VideoControls.exe while running).

call "%~dp0build.bat" || exit /b 1

set "PLUGINS=%APPDATA%\HotSpot\StreamDock\Plugins"
set "DEST=%PLUGINS%\com.local.videocontrols.sdPlugin"
taskkill /im VideoControls.exe /f >nul 2>&1
xcopy "%~dp0com.local.videocontrols.sdPlugin" "%DEST%\" /E /I /Y >nul
if errorlevel 1 (
  echo Copy failed. Check that this folder exists: %PLUGINS%
  exit /b 1
)
echo Installed to %DEST%
echo Now start the AJAZZ Stream Dock app and look for the "Video Controls" category.
