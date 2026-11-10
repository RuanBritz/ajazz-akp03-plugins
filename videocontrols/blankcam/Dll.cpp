// DLL exports, class factory, CBlankActivate and COM self-registration (regsvr32).

#include "BlankCamera.h"
#include <new>

volatile LONG g_dllObjects = 0;
static HMODULE g_module = nullptr;

// ---------------------------------------------------------------------------------------------------------------
// CBlankActivate
// ---------------------------------------------------------------------------------------------------------------

CBlankActivate::CBlankActivate() : refs(1), attrs(nullptr), source(nullptr)
{
    InitializeCriticalSection(&lock);
    InterlockedIncrement(&g_dllObjects);
}

CBlankActivate::~CBlankActivate()
{
    SafeRelease(source);
    SafeRelease(attrs);
    DeleteCriticalSection(&lock);
    InterlockedDecrement(&g_dllObjects);
}

HRESULT CBlankActivate::Create(REFIID riid, void** ppv)
{
    *ppv = nullptr;
    CBlankActivate* a = new (std::nothrow) CBlankActivate();
    if (!a) return E_OUTOFMEMORY;
    HRESULT hr = MFCreateAttributes(&a->attrs, 4);
    if (SUCCEEDED(hr)) hr = a->QueryInterface(riid, ppv);
    a->Release();
    return hr;
}

STDMETHODIMP CBlankActivate::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFAttributes) || riid == __uuidof(IMFActivate))
    {
        *ppv = static_cast<IMFActivate*>(this);
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) CBlankActivate::AddRef() { return InterlockedIncrement(&refs); }

STDMETHODIMP_(ULONG) CBlankActivate::Release()
{
    ULONG n = InterlockedDecrement(&refs);
    if (n == 0) delete this;
    return n;
}

STDMETHODIMP CBlankActivate::ActivateObject(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    EnterCriticalSection(&lock);
    HRESULT hr = S_OK;
    if (!source) hr = CBlankSource::Create(&source);
    if (SUCCEEDED(hr)) hr = source->QueryInterface(riid, ppv);
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankActivate::ShutdownObject()
{
    EnterCriticalSection(&lock);
    if (source) { source->Shutdown(); SafeRelease(source); }
    LeaveCriticalSection(&lock);
    return S_OK;
}

STDMETHODIMP CBlankActivate::DetachObject()
{
    EnterCriticalSection(&lock);
    SafeRelease(source);
    LeaveCriticalSection(&lock);
    return S_OK;
}

// ---------------------------------------------------------------------------------------------------------------
// Class factory
// ---------------------------------------------------------------------------------------------------------------

class CBlankClassFactory : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK; // static object: no ref counting
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        return CBlankActivate::Create(riid, ppv);
    }

    STDMETHODIMP LockServer(BOOL lockIt) override
    {
        if (lockIt) InterlockedIncrement(&g_dllObjects);
        else InterlockedDecrement(&g_dllObjects);
        return S_OK;
    }
};

static CBlankClassFactory g_factory;

// ---------------------------------------------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------------------------------------------

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return g_dllObjects == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(clsid, CLSID_BlankCamera)) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

static const wchar_t kClsidKey[] = L"Software\\Classes\\CLSID\\" BLANKCAM_CLSID_STRING;

static HRESULT SetStringValue(HKEY key, const wchar_t* name, const wchar_t* value)
{
    DWORD bytes = (DWORD)((wcslen(value) + 1) * sizeof(wchar_t));
    LONG r = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), bytes);
    return HRESULT_FROM_WIN32(r);
}

// HKLM\Software\Classes\CLSID\{...}\InprocServer32 = <path>, ThreadingModel = Both.
// Machine-wide so Frame Server (running as LOCAL SERVICE) can find it. Needs admin.
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    DWORD len = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
    if (len == 0 || len >= ARRAYSIZE(path)) return HRESULT_FROM_WIN32(GetLastError());

    HKEY clsidKey = nullptr, inproc = nullptr;
    LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kClsidKey, 0, nullptr, 0, KEY_WRITE, nullptr, &clsidKey, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);

    HRESULT hr = SetStringValue(clsidKey, nullptr, BLANKCAM_FRIENDLY_NAME);
    if (SUCCEEDED(hr))
    {
        r = RegCreateKeyExW(clsidKey, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &inproc, nullptr);
        hr = HRESULT_FROM_WIN32(r);
    }
    if (SUCCEEDED(hr)) hr = SetStringValue(inproc, nullptr, path);
    if (SUCCEEDED(hr)) hr = SetStringValue(inproc, L"ThreadingModel", L"Both");

    if (inproc) RegCloseKey(inproc);
    RegCloseKey(clsidKey);
    return hr;
}

STDAPI DllUnregisterServer()
{
    LONG r = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey);
    return (r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND) ? S_OK : HRESULT_FROM_WIN32(r);
}
