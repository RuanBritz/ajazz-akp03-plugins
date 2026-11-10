// BlankCamSetup.exe install | uninstall
// Creates (or removes) the "Blank Camera" system-wide virtual camera backed by BlankCamera.dll.
// The DLL must already be registered (regsvr32) from a location LOCAL SERVICE can read. Run elevated.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mfapi.h>
#include <mfvirtualcamera.h>
#include <stdio.h>
#include <wchar.h>

#include "BlankCamera.h"

static HRESULT CreateCamera(IMFVirtualCamera** camera)
{
    // System lifetime: survives reboots and doesn't need this process (or the plugin) to stay running.
    return MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_System,
                                 MFVirtualCameraAccess_AllUsers, BLANKCAM_FRIENDLY_NAME, BLANKCAM_CLSID_STRING,
                                 nullptr, 0, camera);
}

static int Fail(const char* step, HRESULT hr)
{
    fprintf(stderr, "%s failed: 0x%08lX\n", step, (unsigned long)hr);
    if (hr == E_ACCESSDENIED) fprintf(stderr, "Run this from an elevated (administrator) prompt.\n");
    return 1;
}

static int Install()
{
    IMFVirtualCamera* camera = nullptr;
    HRESULT hr = CreateCamera(&camera);
    if (FAILED(hr)) return Fail("MFCreateVirtualCamera", hr);

    hr = camera->Start(nullptr);
    if (FAILED(hr))
    {
        camera->Shutdown();
        camera->Release();
        return Fail("IMFVirtualCamera::Start", hr);
    }

    camera->Shutdown(); // the camera itself stays registered (system lifetime)
    camera->Release();
    printf("Blank Camera installed.\n");
    return 0;
}

static int Uninstall()
{
    IMFVirtualCamera* camera = nullptr;
    HRESULT hr = CreateCamera(&camera);
    if (FAILED(hr)) return Fail("MFCreateVirtualCamera", hr);

    hr = camera->Remove();
    camera->Shutdown();
    camera->Release();
    if (FAILED(hr)) return Fail("IMFVirtualCamera::Remove", hr);

    printf("Blank Camera removed.\n");
    return 0;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2 || (_wcsicmp(argv[1], L"install") != 0 && _wcsicmp(argv[1], L"uninstall") != 0))
    {
        fprintf(stderr, "Usage: BlankCamSetup.exe install | uninstall\n");
        return 2;
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return Fail("CoInitializeEx", hr);
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) { CoUninitialize(); return Fail("MFStartup", hr); }

    int rc = _wcsicmp(argv[1], L"install") == 0 ? Install() : Uninstall();

    MFShutdown();
    CoUninitialize();
    return rc;
}
