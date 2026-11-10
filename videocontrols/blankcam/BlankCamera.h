// Blank Camera: a Media Foundation software camera source that outputs solid black frames.
// Windows' Frame Server loads BlankCamera.dll and creates the source through CBlankActivate.
// Modelled on Microsoft's Windows-Camera "SimpleMediaSource" sample, without WRL/WIL so it builds with
// plain cl.exe + the Windows SDK.

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <ks.h>

// IKsControl, declared here rather than via ksproxy.h (which pulls in DirectShow). Same IID and vtable.
struct DECLSPEC_NOVTABLE IBlankKsControl : public IUnknown
{
    STDMETHOD(KsProperty)(PKSPROPERTY prop, ULONG propLen, LPVOID data, ULONG dataLen, ULONG* returned) PURE;
    STDMETHOD(KsMethod)(PKSMETHOD method, ULONG methodLen, LPVOID data, ULONG dataLen, ULONG* returned) PURE;
    STDMETHOD(KsEvent)(PKSEVENT event, ULONG eventLen, LPVOID data, ULONG dataLen, ULONG* returned) PURE;
};

// Keep in sync with BlankCamSetup.cpp and the README.
// {C3A624EB-0E0C-41DE-8BF4-018E74DC0E86}
static const CLSID CLSID_BlankCamera =
    { 0xc3a624eb, 0x0e0c, 0x41de, { 0x8b, 0xf4, 0x01, 0x8e, 0x74, 0xdc, 0x0e, 0x86 } };
#define BLANKCAM_CLSID_STRING L"{C3A624EB-0E0C-41DE-8BF4-018E74DC0E86}"
#define BLANKCAM_FRIENDLY_NAME L"Blank Camera"

// Defined locally so we do not depend on ksproxy.h / ksmedia.h for these GUIDs.
// IKsControl: {28F54685-06FD-11D2-B27A-00A0C9223196}
static const IID IID_BlankKsControl =
    { 0x28f54685, 0x06fd, 0x11d2, { 0xb2, 0x7a, 0x00, 0xa0, 0xc9, 0x22, 0x31, 0x96 } };
// PINNAME_VIDEO_CAPTURE: {FB6C4281-0353-11D1-905F-0000C0CC16BA}
static const GUID PINNAME_BlankVideoCapture =
    { 0xfb6c4281, 0x0353, 0x11d1, { 0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba } };

extern volatile LONG g_dllObjects; // live COM objects + server locks, for DllCanUnloadNow

class CBlankSource;

/// The one video stream. Hands out a black frame per RequestSample, paced at the negotiated frame rate.
class CBlankStream : public IMFMediaStream2, public IBlankKsControl, public IMFAsyncCallback
{
public:
    static HRESULT Create(CBlankSource* source, IMFStreamDescriptor* desc, CBlankStream** out);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** desc) override;
    STDMETHODIMP RequestSample(IUnknown* token) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

    // IKsControl
    STDMETHODIMP KsProperty(PKSPROPERTY prop, ULONG propLen, LPVOID data, ULONG dataLen, ULONG* returned) override;
    STDMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLen, LPVOID data, ULONG dataLen, ULONG* returned) override;
    STDMETHODIMP KsEvent(PKSEVENT event, ULONG eventLen, LPVOID data, ULONG dataLen, ULONG* returned) override;

    // IMFAsyncCallback (frame pacing timer)
    STDMETHODIMP GetParameters(DWORD* flags, DWORD* queue) override;
    STDMETHODIMP Invoke(IMFAsyncResult* result) override;

    // Called by the source.
    HRESULT Start();
    HRESULT Stop();
    void Shutdown();
    HRESULT GetAttributes(IMFAttributes** out);

private:
    CBlankStream();
    ~CBlankStream();
    HRESULT Initialize(CBlankSource* source, IMFStreamDescriptor* desc);
    HRESULT ReadCurrentFormat();
    HRESULT DeliverSample(IUnknown* token);
    HRESULT CheckShutdown() const { return shutdown ? MF_E_SHUTDOWN : S_OK; }

    LONG refs;
    CRITICAL_SECTION lock;
    bool shutdown;
    MF_STREAM_STATE state;
    CBlankSource* source;          // weak: the source owns the stream
    IMFMediaEventQueue* events;
    IMFStreamDescriptor* descriptor;
    IMFAttributes* attributes;

    // Current format.
    GUID subtype;
    UINT32 width, height;
    LONGLONG frameDuration;        // 100 ns units
    BYTE* frame;                   // one pre-filled black frame
    DWORD frameBytes;

    // Pacing: one timer at a time; requests that arrive while it is pending are queued as tokens.
    LONGLONG nextDue;              // MFGetSystemTime of the next frame
    MFWORKITEM_KEY timerKey;
    bool timerPending;
    IUnknown* pendingTokens[8];
    int pendingCount;
};

/// The media source Frame Server talks to.
class CBlankSource : public IMFMediaSourceEx, public IMFGetService, public IBlankKsControl, public IMFSampleAllocatorControl
{
public:
    static HRESULT Create(CBlankSource** out);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* characteristics) override;
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** pd) override;
    STDMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPos) override;
    STDMETHODIMP Stop() override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
    STDMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override;
    STDMETHODIMP SetD3DManager(IUnknown* manager) override;

    // IMFGetService
    STDMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;

    // IKsControl
    STDMETHODIMP KsProperty(PKSPROPERTY prop, ULONG propLen, LPVOID data, ULONG dataLen, ULONG* returned) override;
    STDMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLen, LPVOID data, ULONG dataLen, ULONG* returned) override;
    STDMETHODIMP KsEvent(PKSEVENT event, ULONG eventLen, LPVOID data, ULONG dataLen, ULONG* returned) override;

    // IMFSampleAllocatorControl: we always allocate our own system-memory samples.
    STDMETHODIMP SetDefaultAllocator(DWORD streamId, IUnknown* allocator) override;
    STDMETHODIMP GetAllocatorUsage(DWORD streamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) override;

private:
    CBlankSource();
    ~CBlankSource();
    HRESULT Initialize();
    HRESULT CheckShutdown() const { return shutdown ? MF_E_SHUTDOWN : S_OK; }

    LONG refs;
    CRITICAL_SECTION lock;
    bool shutdown;
    bool mfStarted;
    bool started;
    bool streamAnnounced;          // MENewStream sent once; later starts send MEUpdatedStream
    IMFMediaEventQueue* events;
    IMFAttributes* attributes;
    IMFPresentationDescriptor* presentation;
    CBlankStream* stream;
};

/// IMFActivate handed out by the class factory. Frame Server sets attributes on it, then calls ActivateObject.
class CBlankActivate : public IMFActivate
{
public:
    static HRESULT Create(REFIID riid, void** ppv);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFAttributes (forwarded to an inner store)
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return attrs->GetItem(k, v); }
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return attrs->GetItemType(k, t); }
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return attrs->CompareItem(k, v, r); }
    STDMETHODIMP Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return attrs->Compare(t, m, r); }
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return attrs->GetUINT32(k, v); }
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return attrs->GetUINT64(k, v); }
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return attrs->GetDouble(k, v); }
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return attrs->GetGUID(k, v); }
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* l) override { return attrs->GetStringLength(k, l); }
    STDMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 s, UINT32* l) override { return attrs->GetString(k, v, s, l); }
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* v, UINT32* l) override { return attrs->GetAllocatedString(k, v, l); }
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* s) override { return attrs->GetBlobSize(k, s); }
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 s, UINT32* l) override { return attrs->GetBlob(k, b, s, l); }
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* s) override { return attrs->GetAllocatedBlob(k, b, s); }
    STDMETHODIMP GetUnknown(REFGUID k, REFIID r, LPVOID* p) override { return attrs->GetUnknown(k, r, p); }
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return attrs->SetItem(k, v); }
    STDMETHODIMP DeleteItem(REFGUID k) override { return attrs->DeleteItem(k); }
    STDMETHODIMP DeleteAllItems() override { return attrs->DeleteAllItems(); }
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return attrs->SetUINT32(k, v); }
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return attrs->SetUINT64(k, v); }
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return attrs->SetDouble(k, v); }
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return attrs->SetGUID(k, v); }
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return attrs->SetString(k, v); }
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 s) override { return attrs->SetBlob(k, b, s); }
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* p) override { return attrs->SetUnknown(k, p); }
    STDMETHODIMP LockStore() override { return attrs->LockStore(); }
    STDMETHODIMP UnlockStore() override { return attrs->UnlockStore(); }
    STDMETHODIMP GetCount(UINT32* c) override { return attrs->GetCount(c); }
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return attrs->GetItemByIndex(i, k, v); }
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return attrs->CopyAllItems(d); }

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    STDMETHODIMP ShutdownObject() override;
    STDMETHODIMP DetachObject() override;

private:
    CBlankActivate();
    ~CBlankActivate();

    LONG refs;
    CRITICAL_SECTION lock;
    IMFAttributes* attrs;
    CBlankSource* source;
};

template <class T> inline void SafeRelease(T*& p)
{
    if (p) { p->Release(); p = nullptr; }
}
