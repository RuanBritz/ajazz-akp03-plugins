@echo off
setlocal
rem Builds BlankCamera.dll and BlankCamSetup.exe from blankcam\ into blankcam\bin\.
rem Needs Visual Studio's "Desktop development with C++" workload with a Windows 11 SDK.
rem The plugin itself (build.bat) does not need any of this.

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo Visual Studio Installer not found. Install Visual Studio with "Desktop development with C++".
  exit /b 1
)

set "VSDIR="
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
  echo The C++ build tools are not installed. In the Visual Studio Installer, add
  echo "Desktop development with C++" including a Windows 11 SDK, then run this again.
  exit /b 1
)

call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1 || exit /b 1

set "SRC=%~dp0blankcam"
set "OUT=%SRC%\bin"
set "OBJ=%SRC%\obj"
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OBJ%" mkdir "%OBJ%"

set "CFLAGS=/nologo /W4 /EHsc /O2 /MT /DUNICODE /D_UNICODE"
set "LIBS=mfplat.lib mfuuid.lib ole32.lib advapi32.lib"

rem Compile from obj\ so the .obj files land there.
pushd "%OBJ%"

cl %CFLAGS% /LD "%SRC%\Dll.cpp" "%SRC%\MediaSource.cpp" /Fe"%OUT%\BlankCamera.dll" /link /DEF:"%SRC%\BlankCamera.def" %LIBS%
if errorlevel 1 (
  echo BlankCamera.dll build failed.
  popd
  exit /b 1
)

cl %CFLAGS% "%SRC%\BlankCamSetup.cpp" /Fe"%OUT%\BlankCamSetup.exe" /link mfsensorgroup.lib %LIBS%
if errorlevel 1 (
  echo BlankCamSetup.exe build failed.
  popd
  exit /b 1
)
popd

echo Built: %OUT%\BlankCamera.dll
echo Built: %OUT%\BlankCamSetup.exe
