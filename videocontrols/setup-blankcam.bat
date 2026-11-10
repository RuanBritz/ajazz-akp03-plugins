@echo off
setlocal EnableExtensions
rem Installs or removes the Blank Camera virtual camera. Asks for admin rights once.
rem   setup-blankcam.bat             menu
rem   setup-blankcam.bat install     install / reinstall without the menu
rem   setup-blankcam.bat uninstall   remove without the menu
rem Run build-blankcam.bat first.

set "MODE=%~1"
if defined MODE if /i not "%MODE%"=="install" if /i not "%MODE%"=="uninstall" (
  echo Usage: setup-blankcam.bat [install ^| uninstall]
  exit /b 2
)

rem Relaunch elevated (one UAC prompt); the new window does the work.
net session >nul 2>&1
if errorlevel 1 (
  echo Requesting administrator rights...
  if defined MODE (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%MODE%' -Verb RunAs"
  ) else (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  )
  if errorlevel 1 echo The admin prompt was cancelled; nothing was changed.
  exit /b
)

set "BIN=%~dp0blankcam\bin"
set "DEST=%ProgramFiles%\VideoControls\BlankCamera"
set "CLSIDKEY=HKLM\Software\Classes\CLSID\{C3A624EB-0E0C-41DE-8BF4-018E74DC0E86}\InprocServer32"

if /i "%MODE%"=="install" (call :install & goto end)
if /i "%MODE%"=="uninstall" (call :uninstall & goto end)

:menu
cls
echo.
echo   Blank Camera setup
echo   ------------------
call :show_status
echo.
echo   1  Install / reinstall
echo   2  Uninstall
echo   Q  Quit
echo.
choice /c 12Q /n /m "  Choose 1, 2 or Q: "
if errorlevel 3 exit /b 0
if errorlevel 2 (call :uninstall & goto again)
call :install

:again
echo.
pause
goto menu

:end
echo.
pause
exit /b %RESULT%

rem ---------------------------------------------------------------------------------------------------------------

:show_status
if exist "%BIN%\BlankCamera.dll" (echo   Built:      yes  - %BIN%) else (echo   Built:      no   - run build-blankcam.bat first)
set "INSTALLED=no"
if exist "%DEST%\BlankCamera.dll" reg query "%CLSIDKEY%" >nul 2>&1 && set "INSTALLED=yes"
if "%INSTALLED%"=="yes" (echo   Installed:  yes  - %DEST%) else (echo   Installed:  no)
exit /b 0

:install
echo.
echo Installing Blank Camera
echo.
set "RESULT=1"
if not exist "%BIN%\BlankCamera.dll" (
  echo   [FAILED] %BIN%\BlankCamera.dll not found. Run build-blankcam.bat first.
  exit /b 1
)
if not exist "%BIN%\BlankCamSetup.exe" (
  echo   [FAILED] %BIN%\BlankCamSetup.exe not found. Run build-blankcam.bat first.
  exit /b 1
)

echo   [1/4] Removing any previous Blank Camera...
if exist "%DEST%\BlankCamSetup.exe" (
  "%DEST%\BlankCamSetup.exe" uninstall >nul 2>&1
  echo         done
) else (
  echo         none installed
)

echo   [2/4] Copying files to %DEST%...
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%BIN%\BlankCamera.dll" "%DEST%\" >nul 2>&1 || goto copy_failed
copy /y "%BIN%\BlankCamSetup.exe" "%DEST%\" >nul 2>&1 || goto copy_failed
echo         OK

echo   [3/4] Registering BlankCamera.dll...
regsvr32 /s "%DEST%\BlankCamera.dll"
if errorlevel 1 (
  echo   [FAILED] regsvr32 returned %errorlevel%.
  exit /b 1
)
echo         OK

echo   [4/4] Creating the camera...
"%DEST%\BlankCamSetup.exe" install
if errorlevel 1 (
  echo   [FAILED] See the error above.
  exit /b 1
)

echo.
echo   Blank Camera is installed. It survives reboots.
echo   Restart NVIDIA Broadcast if "Blank Camera" isn't in its camera list yet.
set "RESULT=0"
exit /b 0

:copy_failed
echo   [FAILED] Could not copy to %DEST%. Is the camera in use? Close apps using it and try again.
exit /b 1

:uninstall
echo.
echo Uninstalling Blank Camera
echo.
set "RESULT=0"
if not exist "%DEST%\BlankCamSetup.exe" (
  echo   Not installed - nothing to do.
  exit /b 0
)

echo   [1/3] Removing the camera...
"%DEST%\BlankCamSetup.exe" uninstall
if errorlevel 1 (
  echo         [WARNING] continuing anyway
  set "RESULT=1"
)

echo   [2/3] Unregistering BlankCamera.dll...
regsvr32 /s /u "%DEST%\BlankCamera.dll"
if errorlevel 1 (echo         [WARNING] regsvr32 returned %errorlevel%) else (echo         OK)

echo   [3/3] Deleting %DEST%...
rem Frame Server may still hold the DLL for a moment after the camera is removed.
rmdir /s /q "%DEST%" 2>nul
rmdir "%ProgramFiles%\VideoControls" 2>nul
if exist "%DEST%" (
  echo         [WARNING] Some files are still in use. Delete the folder after a reboot.
) else (
  echo         OK
)

echo.
echo   Blank Camera is uninstalled.
exit /b 0
