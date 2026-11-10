@echo off
setlocal
rem Removes the installed plugin. Close the AJAZZ Stream Dock app first, or it restarts AudioControls.exe.

tasklist /fi "imagename eq Stream Dock AJAZZ.exe" 2>nul | find /i "Stream Dock AJAZZ.exe" >nul
if not errorlevel 1 (
  echo Stream Dock is still running. Close it ^(check the system tray too^) and run this again.
  exit /b 1
)

set "DEST=%APPDATA%\HotSpot\StreamDock\Plugins\com.local.audiocontrols.sdPlugin"
if not exist "%DEST%" (
  echo Not installed: %DEST%
  exit /b 0
)

taskkill /im AudioControls.exe /f >nul 2>&1
rmdir /s /q "%DEST%"
if exist "%DEST%" (
  echo Could not fully remove %DEST% - a file may still be in use.
  exit /b 1
)
echo Removed %DEST%
echo Keys you placed from this plugin will show as blank in Stream Dock; delete them there.
