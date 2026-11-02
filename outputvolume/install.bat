@echo off
setlocal
rem Close the AJAZZ Stream Dock app before running this (it locks OutputVolume.exe while running).

call "%~dp0build.bat" || exit /b 1

set "DEST=%APPDATA%\HotSpot\StreamDock\Plugins\com.local.outputvolume.sdPlugin"
taskkill /im OutputVolume.exe /f >nul 2>&1
xcopy "%~dp0com.local.outputvolume.sdPlugin" "%DEST%\" /E /I /Y >nul
if errorlevel 1 (
  echo Copy failed. Check that this folder exists: %APPDATA%\HotSpot\StreamDock\Plugins
  exit /b 1
)
echo Installed to %DEST%
echo Now start the AJAZZ Stream Dock app and look for the "Output Volume" category.
