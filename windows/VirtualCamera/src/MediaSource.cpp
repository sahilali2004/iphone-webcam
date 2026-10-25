#include "MediaSource.h"

#include "MediaStream.h"

MediaSource::~MediaSource() = default;

STDMETHODIMP MediaSource::QueryInterface(REFIID riid, void** ppv)
{
    RETURN_HR_IF(E_POINTER, !ppv);
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == __uuidof(IMFMediaEventGenerator) || riid == __uuidof(IMFMediaSource) ||
        riid == __uuidof(IMFMediaSourceEx)) {
        *ppv = static_cast<IMFMediaSourceEx*>(this);
    } else if (riid == __uuidof(IMFAttributes)) {
        *ppv = static_cast<IMFAttributes*>(this);
    } else if (riid == __uuidof(IMFGetService)) {
        *ppv = static_cast<IMFGetService*>(this);
    } else if (riid == __uuidof(IKsControl)) {
        *ppv = static_cast<IKsControl*>(this);
    } else if (riid == __uuidof(IMFSampleAllocatorControl)) {
        *ppv = static_cast<IMFSampleAllocatorControl*>(this);
    } else {
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

HRESULT MediaSource::Initialize(IMFAttributes* activatorAttributes)
{
    RETURN_HR_IF(E_OUTOFMEMORY, !_attributes);
    if (activatorAttributes) RETURN_IF_FAILED(activatorAttributes->CopyAllItems(this));

    // Sensor profiles: a legacy profile (≤30 fps) lets apps that ask for profiles find us.
    ComPtr<IMFSensorProfileCollection> profiles;
    ComPtr<IMFSensorProfile> profile;
    RETURN_IF_FAILED(MFCreateSensorProfileCollection(&profiles));
    RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, &profile));
    RETURN_IF_FAILED(profile->AddProfileFilter(0, L"((RES==;FRT<=30,1;SUT==))"));
    RETURN_IF_FAILED(profiles->AddProfile(profile.Get()));
    RETURN_IF_FAILED(SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, profiles.Get()));

    _stream.Attach(new (std::nothrow) MediaStream());
    RETURN_HR_IF(E_OUTOFMEMORY, !_stream);
    RETURN_IF_FAILED(_stream->Initialize(this, 0));

    ComPtr<IMFStreamDescriptor> streamDescriptor;
    RETURN_IF_FAILED(_stream->GetStreamDescriptor(&streamDescriptor));
    IMFStreamDescriptor* descriptors[] = {streamDescriptor.Get()};
    RETURN_IF_FAILED(MFCreatePresentationDescriptor(1, descriptors, &_descriptor));
    RETURN_IF_FAILED(MFCreateEventQueue(&_queue));
    Trace(L"MediaSource initialized");
    return S_OK;
}

// IMFMediaEventGenerator

STDMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->BeginGetEvent(callback, state);
}

STDMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->EndGetEvent(result, event);
}

STDMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(_lock);
        queue = _queue;
    }
    RETURN_HR_IF(MF_E_SHUTDOWN, !queue);
    return queue->GetEvent(flags, event);
}

STDMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value)
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->QueueEventParamVar(type, extendedType, status, value);
}

// IMFMediaSource

STDMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor)
{
    RETURN_HR_IF(E_POINTER, !descriptor);
    *descriptor = nullptr;
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_descriptor);
    return _descriptor->Clone(descriptor);
}

STDMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics)
{
    RETURN_HR_IF(E_POINTER, !characteristics);
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

STDMETHODIMP MediaSource::Pause()
{
    return MF_E_INVALID_STATE_TRANSITION;  // live source
}

STDMETHODIMP MediaSource::Shutdown()
{
    Trace(L"MediaSource::Shutdown");
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    _queue->Shutdown();
    _queue.Reset();
    if (_stream) _stream->Shutdown();
    _descriptor.Reset();
    return S_OK;
}

STDMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition)
{
    RETURN_HR_IF(E_POINTER, !descriptor || !startPosition);
    RETURN_HR_IF(MF_E_UNSUPPORTED_TIME_FORMAT, timeFormat && *timeFormat != GUID_NULL);
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_descriptor);

    DWORD count = 0;
    RETURN_IF_FAILED(descriptor->GetStreamDescriptorCount(&count));
    RETURN_HR_IF(E_INVALIDARG, count != 1);

    BOOL requested = FALSE;
    ComPtr<IMFStreamDescriptor> streamDescriptor;
    RETURN_IF_FAILED(descriptor->GetStreamDescriptorByIndex(0, &requested, &streamDescriptor));

    MF_STREAM_STATE state;
    RETURN_IF_FAILED(_stream->GetStreamState(&state));
    const BOOL running = state != MF_STREAM_STATE_STOPPED;

    if (requested && !running) {
        RETURN_IF_FAILED(_descriptor->SelectStream(0));
        ComPtr<IUnknown> unknown;
        RETURN_IF_FAILED(_stream.As(&unknown));
        RETURN_IF_FAILED(_queue->QueueEventParamUnk(MENewStream, GUID_NULL, S_OK, unknown.Get()));

        // Start with the media type the caller negotiated on its descriptor.
        ComPtr<IMFMediaTypeHandler> handler;
        ComPtr<IMFMediaType> type;
        RETURN_IF_FAILED(streamDescriptor->GetMediaTypeHandler(&handler));
        RETURN_IF_FAILED(handler->GetCurrentMediaType(&type));
        RETURN_IF_FAILED(_stream->Start(type.Get()));
    } else if (!requested && running) {
        RETURN_IF_FAILED(_descriptor->DeselectStream(0));
        RETURN_IF_FAILED(_stream->Stop());
    }

    PROPVARIANT time;
    RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &time));
    HRESULT hr = _queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
    PropVariantClear(&time);
    return hr;
}

STDMETHODIMP MediaSource::Stop()
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_descriptor);
    RETURN_IF_FAILED(_stream->Stop());
    RETURN_IF_FAILED(_descriptor->DeselectStream(0));

    PROPVARIANT time;
    RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &time));
    HRESULT hr = _queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &time);
    PropVariantClear(&time);
    return hr;
}

// IMFMediaSourceEx

STDMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes)
{
    RETURN_HR_IF(E_POINTER, !attributes);
    *attributes = static_cast<IMFAttributes*>(this);
    AddRef();
    return S_OK;
}

STDMETHODIMP MediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes)
{
    RETURN_HR_IF(E_POINTER, !attributes);
    *attributes = nullptr;
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_INVALIDSTREAMNUMBER, streamId != 0 || !_stream);
    return _stream->QueryInterface(IID_PPV_ARGS(attributes));
}

STDMETHODIMP MediaSource::SetD3DManager(IUnknown*)
{
    // CPU path: frames are written into system-memory samples. GPU path is a later optimisation.
    return S_OK;
}

// IMFGetService

STDMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* ppv)
{
    if (ppv) *ppv = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

// IMFSampleAllocatorControl

STDMETHODIMP MediaSource::SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator)
{
    RETURN_HR_IF(MF_E_INVALIDSTREAMNUMBER, outputStreamId != 0);
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_stream);
    return _stream->SetAllocator(allocator);
}

STDMETHODIMP MediaSource::GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage)
{
    RETURN_HR_IF(E_POINTER, !inputStreamId || !usage);
    RETURN_HR_IF(MF_E_INVALIDSTREAMNUMBER, outputStreamId != 0);
    *inputStreamId = outputStreamId;
    *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
    return S_OK;
}

// IKsControl: no camera controls exposed yet.

STDMETHODIMP MediaSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP MediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP MediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
