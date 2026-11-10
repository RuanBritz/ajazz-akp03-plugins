// CBlankSource and CBlankStream: one live video stream of black frames.

// initguid.h defines the MF GUIDs used here in this file (selectany), so newer ones such as
// MF_DEVICESTREAM_FRAMESERVER_SHARED don't depend on what this SDK's mfuuid.lib contains.
#include <initguid.h>
#include "BlankCamera.h"
#include <new>
#include <string.h>

namespace
{
    // Formats offered, in order of preference. NV12 first (what cameras and NVIDIA Broadcast normally use),
    // with RGB32 as a fallback for consumers that refuse NV12. 1080p first to match the Brio 100.
    struct Format { const GUID* subtype; UINT32 width, height, fps; };
    const Format kFormats[] =
    {
        { &MFVideoFormat_NV12,  1920, 1080, 30 },
        { &MFVideoFormat_NV12,  1280,  720, 30 },
        { &MFVideoFormat_RGB32, 1920, 1080, 30 },
        { &MFVideoFormat_RGB32, 1280,  720, 30 },
    };
    const DWORD kFormatCount = ARRAYSIZE(kFormats);
    const DWORD kStreamId = 0;

    HRESULT CreateMediaType(const Format& f, IMFMediaType** out)
    {
        *out = nullptr;
        IMFMediaType* type = nullptr;
        HRESULT hr = MFCreateMediaType(&type);
        if (FAILED(hr)) return hr;

        bool nv12 = IsEqualGUID(*f.subtype, MFVideoFormat_NV12) != 0;
        UINT32 stride = nv12 ? f.width : f.width * 4;
        UINT32 size = nv12 ? f.width * f.height * 3 / 2 : f.width * f.height * 4;

        if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, *f.subtype);
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(type, MF_MT_FRAME_SIZE, f.width, f.height);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type, MF_MT_FRAME_RATE, f.fps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
        if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
        if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_DEFAULT_STRIDE, stride);
        if (SUCCEEDED(hr)) hr = type->SetUINT32(MF_MT_SAMPLE_SIZE, size);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type, MF_MT_FRAME_RATE_RANGE_MAX, f.fps, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(type, MF_MT_FRAME_RATE_RANGE_MIN, f.fps, 1);

        if (SUCCEEDED(hr)) *out = type;
        else type->Release();
        return hr;
    }

    // Marks a stream (descriptor or attribute store) as a shareable colour capture pin, as Frame Server expects.
    HRESULT SetCaptureStreamAttributes(IMFAttributes* a)
    {
        HRESULT hr = a->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_BlankVideoCapture);
        if (SUCCEEDED(hr)) hr = a->SetUINT32(MF_DEVICESTREAM_STREAM_ID, kStreamId);
        if (SUCCEEDED(hr)) hr = a->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
        if (SUCCEEDED(hr)) hr = a->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);
        return hr;
    }

    const HRESULT kKsNotFound = HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

// ---------------------------------------------------------------------------------------------------------------
// CBlankSource
// ---------------------------------------------------------------------------------------------------------------

CBlankSource::CBlankSource()
    : refs(1), shutdown(false), mfStarted(false), started(false), streamAnnounced(false),
      events(nullptr), attributes(nullptr), presentation(nullptr), stream(nullptr)
{
    InitializeCriticalSection(&lock);
    InterlockedIncrement(&g_dllObjects);
}

CBlankSource::~CBlankSource()
{
    Shutdown();
    DeleteCriticalSection(&lock);
    InterlockedDecrement(&g_dllObjects);
}

HRESULT CBlankSource::Create(CBlankSource** out)
{
    *out = nullptr;
    CBlankSource* s = new (std::nothrow) CBlankSource();
    if (!s) return E_OUTOFMEMORY;
    HRESULT hr = s->Initialize();
    if (FAILED(hr)) { s->Release(); return hr; }
    *out = s;
    return S_OK;
}

HRESULT CBlankSource::Initialize()
{
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;
    mfStarted = true;

    hr = MFCreateEventQueue(&events);
    if (SUCCEEDED(hr)) hr = MFCreateAttributes(&attributes, 4);
    if (FAILED(hr)) return hr;

    IMFMediaType* types[kFormatCount] = {};
    for (DWORD i = 0; i < kFormatCount && SUCCEEDED(hr); i++)
        hr = CreateMediaType(kFormats[i], &types[i]);

    IMFStreamDescriptor* desc = nullptr;
    IMFMediaTypeHandler* handler = nullptr;
    if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(kStreamId, kFormatCount, types, &desc);
    if (SUCCEEDED(hr)) hr = desc->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(types[0]);
    if (SUCCEEDED(hr)) hr = SetCaptureStreamAttributes(desc);
    if (SUCCEEDED(hr)) hr = MFCreatePresentationDescriptor(1, &desc, &presentation);
    if (SUCCEEDED(hr)) hr = presentation->SelectStream(0);
    if (SUCCEEDED(hr)) hr = CBlankStream::Create(this, desc, &stream);

    SafeRelease(handler);
    SafeRelease(desc);
    for (DWORD i = 0; i < kFormatCount; i++) SafeRelease(types[i]);
    return hr;
}

STDMETHODIMP CBlankSource::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEventGenerator) || riid == __uuidof(IMFMediaSource) ||
        riid == __uuidof(IMFMediaSourceEx))
        *ppv = static_cast<IMFMediaSourceEx*>(this);
    else if (riid == __uuidof(IMFGetService))
        *ppv = static_cast<IMFGetService*>(this);
    else if (riid == IID_BlankKsControl)
        *ppv = static_cast<IBlankKsControl*>(this);
    else if (riid == __uuidof(IMFSampleAllocatorControl))
        *ppv = static_cast<IMFSampleAllocatorControl*>(this);
    else
        return E_NOINTERFACE;
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) CBlankSource::AddRef() { return InterlockedIncrement(&refs); }

STDMETHODIMP_(ULONG) CBlankSource::Release()
{
    ULONG n = InterlockedDecrement(&refs);
    if (n == 0) delete this;
    return n;
}

// IMFMediaEventGenerator ----------------------------------------------------------------------------------------

STDMETHODIMP CBlankSource::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    // Don't hold the lock: GetEvent can block.
    IMFMediaEventQueue* q = nullptr;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) { q = events; q->AddRef(); }
    LeaveCriticalSection(&lock);
    if (FAILED(hr)) return hr;
    hr = q->GetEvent(flags, event);
    q->Release();
    return hr;
}

STDMETHODIMP CBlankSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = events->BeginGetEvent(callback, state);
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = events->EndGetEvent(result, event);
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankSource::QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value)
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = events->QueueEventParamVar(type, ext, status, value);
    LeaveCriticalSection(&lock);
    return hr;
}

// IMFMediaSource ------------------------------------------------------------------------------------------------

STDMETHODIMP CBlankSource::GetCharacteristics(DWORD* characteristics)
{
    if (!characteristics) return E_POINTER;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) *characteristics = MFMEDIASOURCE_IS_LIVE;
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankSource::CreatePresentationDescriptor(IMFPresentationDescriptor** pd)
{
    if (!pd) return E_POINTER;
    *pd = nullptr;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = presentation->Clone(pd);
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankSource::Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPos)
{
    if (!pd) return E_INVALIDARG;
    if (timeFormat && !IsEqualGUID(*timeFormat, GUID_NULL)) return MF_E_UNSUPPORTED_TIME_FORMAT;

    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();

    // Our one stream must be selected in the caller's descriptor.
    BOOL selected = FALSE;
    IMFStreamDescriptor* desc = nullptr;
    if (SUCCEEDED(hr)) hr = pd->GetStreamDescriptorByIndex(0, &selected, &desc);
    SafeRelease(desc);
    if (SUCCEEDED(hr) && !selected) hr = MF_E_INVALIDREQUEST;

    if (SUCCEEDED(hr))
    {
        // The caller may have picked a different media type on its copy; mirror the selection on ours.
        presentation->SelectStream(0);
        hr = events->QueueEventParamUnk(streamAnnounced ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
                                        static_cast<IMFMediaStream2*>(stream));
    }
    if (SUCCEEDED(hr))
    {
        streamAnnounced = true;
        hr = stream->Start();
    }
    if (SUCCEEDED(hr))
    {
        PROPVARIANT time;
        PropVariantInit(&time);
        time.vt = VT_I8;
        time.hVal.QuadPart = MFGetSystemTime();
        hr = events->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
        started = SUCCEEDED(hr);
    }

    LeaveCriticalSection(&lock);
    (void)startPos; // live source: always starts "now"
    return hr;
}

STDMETHODIMP CBlankSource::Stop()
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = stream->Stop();
    if (SUCCEEDED(hr))
    {
        started = false;
        hr = events->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
    }
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankSource::Pause()
{
    // Live sources don't pause.
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    LeaveCriticalSection(&lock);
    return SUCCEEDED(hr) ? MF_E_INVALID_STATE_TRANSITION : hr;
}

STDMETHODIMP CBlankSource::Shutdown()
{
    EnterCriticalSection(&lock);
    if (shutdown)
    {
        LeaveCriticalSection(&lock);
        return MF_E_SHUTDOWN;
    }
    shutdown = true;

    if (stream) { stream->Shutdown(); SafeRelease(stream); }
    if (events) events->Shutdown();
    SafeRelease(events);
    SafeRelease(presentation);
    SafeRelease(attributes);
    if (mfStarted) { MFShutdown(); mfStarted = false; }

    LeaveCriticalSection(&lock);
    return S_OK;
}

// IMFMediaSourceEx ----------------------------------------------------------------------------------------------

STDMETHODIMP CBlankSource::GetSourceAttributes(IMFAttributes** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) { *out = attributes; attributes->AddRef(); }
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankSource::GetStreamAttributes(DWORD streamId, IMFAttributes** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (streamId != kStreamId) return MF_E_INVALIDSTREAMNUMBER;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = stream->GetAttributes(out);
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankSource::SetD3DManager(IUnknown*)
{
    // System-memory frames only; nothing to do with a D3D manager.
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    LeaveCriticalSection(&lock);
    return hr;
}

// IMFGetService / IKsControl / IMFSampleAllocatorControl --------------------------------------------------------

STDMETHODIMP CBlankSource::GetService(REFGUID, REFIID, LPVOID* object)
{
    if (object) *object = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

STDMETHODIMP CBlankSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* returned)
{
    if (returned) *returned = 0;
    return kKsNotFound;
}

STDMETHODIMP CBlankSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* returned)
{
    if (returned) *returned = 0;
    return kKsNotFound;
}

STDMETHODIMP CBlankSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* returned)
{
    if (returned) *returned = 0;
    return kKsNotFound;
}

STDMETHODIMP CBlankSource::SetDefaultAllocator(DWORD streamId, IUnknown*)
{
    return streamId == kStreamId ? S_OK : MF_E_INVALIDSTREAMNUMBER;
}

STDMETHODIMP CBlankSource::GetAllocatorUsage(DWORD streamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage)
{
    if (!inputStreamId || !usage) return E_POINTER;
    if (streamId != kStreamId) return MF_E_INVALIDSTREAMNUMBER;
    *inputStreamId = streamId;
    *usage = MFSampleAllocatorUsage_UsesCustomAllocator;
    return S_OK;
}

// ---------------------------------------------------------------------------------------------------------------
// CBlankStream
// ---------------------------------------------------------------------------------------------------------------

CBlankStream::CBlankStream()
    : refs(1), shutdown(false), state(MF_STREAM_STATE_STOPPED), source(nullptr), events(nullptr),
      descriptor(nullptr), attributes(nullptr), subtype(GUID_NULL), width(0), height(0),
      frameDuration(333333), frame(nullptr), frameBytes(0), nextDue(0), timerKey(0), timerPending(false),
      pendingCount(0)
{
    InitializeCriticalSection(&lock);
    InterlockedIncrement(&g_dllObjects);
}

CBlankStream::~CBlankStream()
{
    Shutdown();
    DeleteCriticalSection(&lock);
    InterlockedDecrement(&g_dllObjects);
}

HRESULT CBlankStream::Create(CBlankSource* source, IMFStreamDescriptor* desc, CBlankStream** out)
{
    *out = nullptr;
    CBlankStream* s = new (std::nothrow) CBlankStream();
    if (!s) return E_OUTOFMEMORY;
    HRESULT hr = s->Initialize(source, desc);
    if (FAILED(hr)) { s->Release(); return hr; }
    *out = s;
    return S_OK;
}

HRESULT CBlankStream::Initialize(CBlankSource* src, IMFStreamDescriptor* desc)
{
    source = src;
    descriptor = desc;
    descriptor->AddRef();
    HRESULT hr = MFCreateEventQueue(&events);
    if (SUCCEEDED(hr)) hr = MFCreateAttributes(&attributes, 4);
    if (SUCCEEDED(hr)) hr = SetCaptureStreamAttributes(attributes);
    return hr;
}

HRESULT CBlankStream::GetAttributes(IMFAttributes** out)
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) { *out = attributes; attributes->AddRef(); }
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankStream::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEventGenerator) || riid == __uuidof(IMFMediaStream) ||
        riid == __uuidof(IMFMediaStream2))
        *ppv = static_cast<IMFMediaStream2*>(this);
    else if (riid == IID_BlankKsControl)
        *ppv = static_cast<IBlankKsControl*>(this);
    else if (riid == __uuidof(IMFAsyncCallback))
        *ppv = static_cast<IMFAsyncCallback*>(this);
    else
        return E_NOINTERFACE;
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) CBlankStream::AddRef() { return InterlockedIncrement(&refs); }

STDMETHODIMP_(ULONG) CBlankStream::Release()
{
    ULONG n = InterlockedDecrement(&refs);
    if (n == 0) delete this;
    return n;
}

// IMFMediaEventGenerator ----------------------------------------------------------------------------------------

STDMETHODIMP CBlankStream::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    IMFMediaEventQueue* q = nullptr;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) { q = events; q->AddRef(); }
    LeaveCriticalSection(&lock);
    if (FAILED(hr)) return hr;
    hr = q->GetEvent(flags, event);
    q->Release();
    return hr;
}

STDMETHODIMP CBlankStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* st)
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = events->BeginGetEvent(callback, st);
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = events->EndGetEvent(result, event);
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankStream::QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value)
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = events->QueueEventParamVar(type, ext, status, value);
    LeaveCriticalSection(&lock);
    return hr;
}

// IMFMediaStream ------------------------------------------------------------------------------------------------

STDMETHODIMP CBlankStream::GetMediaSource(IMFMediaSource** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = source ? source->QueryInterface(IID_PPV_ARGS(out)) : MF_E_SHUTDOWN;
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankStream::GetStreamDescriptor(IMFStreamDescriptor** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) { *out = descriptor; descriptor->AddRef(); }
    LeaveCriticalSection(&lock);
    return hr;
}

STDMETHODIMP CBlankStream::RequestSample(IUnknown* token)
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr) && state != MF_STREAM_STATE_RUNNING) hr = MF_E_MEDIA_SOURCE_WRONGSTATE;

    if (SUCCEEDED(hr))
    {
        LONGLONG now = MFGetSystemTime();
        if (!timerPending && pendingCount == 0 && now >= nextDue)
        {
            hr = DeliverSample(token);
        }
        else if (pendingCount < (int)ARRAYSIZE(pendingTokens))
        {
            // Too early for the next frame: hold the request until the frame is due.
            pendingTokens[pendingCount++] = token;
            if (token) token->AddRef();
            if (!timerPending)
            {
                LONGLONG waitMs = (nextDue - now) / 10000;
                if (waitMs < 1) waitMs = 1;
                hr = MFScheduleWorkItem(this, nullptr, -waitMs, &timerKey);
                timerPending = SUCCEEDED(hr);
            }
        }
        else
        {
            hr = MF_E_NOTACCEPTING; // consumer is far ahead of the frame rate
        }
    }

    LeaveCriticalSection(&lock);
    return hr;
}

// IMFMediaStream2 -----------------------------------------------------------------------------------------------

STDMETHODIMP CBlankStream::SetStreamState(MF_STREAM_STATE value)
{
    switch (value)
    {
    case MF_STREAM_STATE_RUNNING: return Start();
    case MF_STREAM_STATE_STOPPED: return Stop();
    case MF_STREAM_STATE_PAUSED:  return MF_E_INVALID_STATE_TRANSITION;
    default:                      return MF_E_INVALID_STATE_TRANSITION;
    }
}

STDMETHODIMP CBlankStream::GetStreamState(MF_STREAM_STATE* value)
{
    if (!value) return E_POINTER;
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) *value = state;
    LeaveCriticalSection(&lock);
    return hr;
}

// IKsControl ----------------------------------------------------------------------------------------------------

STDMETHODIMP CBlankStream::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* returned)
{
    if (returned) *returned = 0;
    return kKsNotFound;
}

STDMETHODIMP CBlankStream::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* returned)
{
    if (returned) *returned = 0;
    return kKsNotFound;
}

STDMETHODIMP CBlankStream::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* returned)
{
    if (returned) *returned = 0;
    return kKsNotFound;
}

// IMFAsyncCallback: the frame-pacing timer fired --------------------------------------------------------------

STDMETHODIMP CBlankStream::GetParameters(DWORD*, DWORD*)
{
    return E_NOTIMPL; // default work queue and flags
}

STDMETHODIMP CBlankStream::Invoke(IMFAsyncResult*)
{
    EnterCriticalSection(&lock);
    timerPending = false;
    if (!shutdown && state == MF_STREAM_STATE_RUNNING && pendingCount > 0)
    {
        IUnknown* token = pendingTokens[0];
        memmove(pendingTokens, pendingTokens + 1, (pendingCount - 1) * sizeof(IUnknown*));
        pendingCount--;
        DeliverSample(token);
        SafeRelease(token);

        if (pendingCount > 0)
        {
            LONGLONG waitMs = (nextDue - MFGetSystemTime()) / 10000;
            if (waitMs < 1) waitMs = 1;
            timerPending = SUCCEEDED(MFScheduleWorkItem(this, nullptr, -waitMs, &timerKey));
        }
    }
    LeaveCriticalSection(&lock);
    return S_OK;
}

// Called by the source ------------------------------------------------------------------------------------------

HRESULT CBlankStream::Start()
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr)) hr = ReadCurrentFormat();
    if (SUCCEEDED(hr))
    {
        state = MF_STREAM_STATE_RUNNING;
        nextDue = MFGetSystemTime();
        hr = events->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
    }
    LeaveCriticalSection(&lock);
    return hr;
}

HRESULT CBlankStream::Stop()
{
    EnterCriticalSection(&lock);
    HRESULT hr = CheckShutdown();
    if (SUCCEEDED(hr))
    {
        state = MF_STREAM_STATE_STOPPED;
        if (timerPending) { MFCancelWorkItem(timerKey); timerPending = false; }
        for (int i = 0; i < pendingCount; i++) SafeRelease(pendingTokens[i]);
        pendingCount = 0;
        hr = events->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }
    LeaveCriticalSection(&lock);
    return hr;
}

void CBlankStream::Shutdown()
{
    EnterCriticalSection(&lock);
    if (!shutdown)
    {
        shutdown = true;
        state = MF_STREAM_STATE_STOPPED;
        if (timerPending) { MFCancelWorkItem(timerKey); timerPending = false; }
        for (int i = 0; i < pendingCount; i++) SafeRelease(pendingTokens[i]);
        pendingCount = 0;
        if (events) events->Shutdown();
        SafeRelease(events);
        SafeRelease(descriptor);
        SafeRelease(attributes);
        delete[] frame;
        frame = nullptr;
        source = nullptr;
    }
    LeaveCriticalSection(&lock);
}

// Internals (lock held) -----------------------------------------------------------------------------------------

/// Reads the media type the consumer selected and pre-renders one black frame in that format.
HRESULT CBlankStream::ReadCurrentFormat()
{
    IMFMediaTypeHandler* handler = nullptr;
    IMFMediaType* type = nullptr;
    HRESULT hr = descriptor->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->GetCurrentMediaType(&type);
    if (FAILED(hr) && handler)
    {
        // Nothing selected yet: use the first (preferred) type.
        hr = handler->GetMediaTypeByIndex(0, &type);
        if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(type);
    }

    GUID sub = GUID_NULL;
    UINT32 w = 0, h = 0, num = 0, den = 0;
    if (SUCCEEDED(hr)) hr = type->GetGUID(MF_MT_SUBTYPE, &sub);
    if (SUCCEEDED(hr)) hr = MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &w, &h);
    if (SUCCEEDED(hr) && FAILED(MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &num, &den))) { num = 30; den = 1; }
    SafeRelease(type);
    SafeRelease(handler);
    if (FAILED(hr)) return hr;
    if (w == 0 || h == 0 || num == 0 || den == 0) return MF_E_INVALIDMEDIATYPE;

    bool nv12 = IsEqualGUID(sub, MFVideoFormat_NV12) != 0;
    if (!nv12 && !IsEqualGUID(sub, MFVideoFormat_RGB32)) return MF_E_INVALIDMEDIATYPE;

    if (frame && IsEqualGUID(sub, subtype) && w == width && h == height)
    {
        frameDuration = (LONGLONG)10000000 * den / num;
        return S_OK; // already rendered
    }

    DWORD bytes = nv12 ? w * h * 3 / 2 : w * h * 4;
    BYTE* buf = new (std::nothrow) BYTE[bytes];
    if (!buf) return E_OUTOFMEMORY;
    if (nv12)
    {
        // Video-range black: Y = 16, Cb = Cr = 128.
        memset(buf, 16, w * h);
        memset(buf + w * h, 128, w * h / 2);
    }
    else
    {
        memset(buf, 0, bytes); // X8R8G8B8 black
    }

    delete[] frame;
    frame = buf;
    frameBytes = bytes;
    subtype = sub;
    width = w;
    height = h;
    frameDuration = (LONGLONG)10000000 * den / num;
    return S_OK;
}

HRESULT CBlankStream::DeliverSample(IUnknown* token)
{
    IMFSample* sample = nullptr;
    IMFMediaBuffer* buffer = nullptr;
    BYTE* dest = nullptr;

    HRESULT hr = MFCreateSample(&sample);
    if (SUCCEEDED(hr)) hr = MFCreateMemoryBuffer(frameBytes, &buffer);
    if (SUCCEEDED(hr)) hr = buffer->Lock(&dest, nullptr, nullptr);
    if (SUCCEEDED(hr))
    {
        memcpy(dest, frame, frameBytes);
        buffer->Unlock();
        hr = buffer->SetCurrentLength(frameBytes);
    }
    if (SUCCEEDED(hr)) hr = sample->AddBuffer(buffer);

    LONGLONG now = MFGetSystemTime();
    if (SUCCEEDED(hr)) hr = sample->SetSampleTime(now);
    if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(frameDuration);
    if (SUCCEEDED(hr) && token) hr = sample->SetUnknown(MFSampleExtension_Token, token);
    if (SUCCEEDED(hr)) hr = events->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample);

    // Next frame one period later; if we fell behind (e.g. consumer paused), restart pacing from now.
    nextDue += frameDuration;
    if (nextDue < now) nextDue = now + frameDuration;

    SafeRelease(buffer);
    SafeRelease(sample);
    return hr;
}
