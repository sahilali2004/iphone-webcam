#include "MediaStream.h"

namespace {

struct Format {
    UINT32 width;
    UINT32 height;
    const GUID* subtype;
};

// First entry is the default. NV12 first: it's what camera pipelines and most apps prefer.
const Format kFormats[] = {
    {1920, 1080, &MFVideoFormat_NV12},
    {1280, 720, &MFVideoFormat_NV12},
    {1920, 1080, &MFVideoFormat_RGB32},
    {1280, 720, &MFVideoFormat_RGB32},
};

HRESULT CreateVideoType(const Format& f, IMFMediaType** result)
{
    ComPtr<IMFMediaType> type;
    RETURN_IF_FAILED(MFCreateMediaType(&type));
    const bool nv12 = *f.subtype == MFVideoFormat_NV12;
    RETURN_IF_FAILED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    RETURN_IF_FAILED(type->SetGUID(MF_MT_SUBTYPE, *f.subtype));
    RETURN_IF_FAILED(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, f.width, f.height));
    RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, 30, 1));
    RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    RETURN_IF_FAILED(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    RETURN_IF_FAILED(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
    RETURN_IF_FAILED(type->SetUINT32(MF_MT_DEFAULT_STRIDE, nv12 ? f.width : f.width * 4));
    const UINT32 frameBytes = nv12 ? f.width * f.height * 3 / 2 : f.width * f.height * 4;
    RETURN_IF_FAILED(type->SetUINT32(MF_MT_SAMPLE_SIZE, frameBytes));
    RETURN_IF_FAILED(type->SetUINT32(MF_MT_AVG_BITRATE, frameBytes * 8 * 30));
    if (nv12) {
        RETURN_IF_FAILED(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
        RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
        RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
    }
    *result = type.Detach();
    return S_OK;
}

}  // namespace

STDMETHODIMP MediaStream::QueryInterface(REFIID riid, void** ppv)
{
    RETURN_HR_IF(E_POINTER, !ppv);
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == __uuidof(IMFMediaEventGenerator) || riid == __uuidof(IMFMediaStream) ||
        riid == __uuidof(IMFMediaStream2)) {
        *ppv = static_cast<IMFMediaStream2*>(this);
    } else if (riid == __uuidof(IMFAttributes)) {
        *ppv = static_cast<IMFAttributes*>(this);
    } else if (riid == __uuidof(IKsControl)) {
        *ppv = static_cast<IKsControl*>(this);
    } else {
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

HRESULT MediaStream::Initialize(IMFMediaSource* source, DWORD id)
{
    RETURN_HR_IF(E_POINTER, !source || !_attributes);
    _source = source;
    _id = id;

    RETURN_IF_FAILED(SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
    RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_STREAM_ID, id));
    RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
    RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
    RETURN_IF_FAILED(MFCreateEventQueue(&_queue));

    constexpr DWORD count = _countof(kFormats);
    IMFMediaType* types[count] = {};
    HRESULT hr = S_OK;
    for (DWORD i = 0; i < count && SUCCEEDED(hr); i++) {
        hr = CreateVideoType(kFormats[i], &types[i]);
    }
    if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(id, count, types, &_descriptor);

    ComPtr<IMFMediaTypeHandler> handler;
    if (SUCCEEDED(hr)) hr = _descriptor->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(types[0]);
    for (auto* type : types) {
        if (type) type->Release();
    }
    return hr;
}

HRESULT MediaStream::SetAllocator(IUnknown* allocator)
{
    RETURN_HR_IF(E_POINTER, !allocator);
    std::lock_guard lock(_lock);
    _allocator.Reset();
    _ownAllocator = false;
    return allocator->QueryInterface(IID_PPV_ARGS(&_allocator));
}

HRESULT MediaStream::Start(IMFMediaType* type)
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_descriptor);

    ComPtr<IMFMediaType> current = type;
    if (!current) {
        ComPtr<IMFMediaTypeHandler> handler;
        RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
        RETURN_IF_FAILED(handler->GetCurrentMediaType(&current));
    }

    GUID subtype = GUID_NULL;
    UINT32 width = 0, height = 0;
    RETURN_IF_FAILED(current->GetGUID(MF_MT_SUBTYPE, &subtype));
    RETURN_IF_FAILED(MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &width, &height));
    RETURN_IF_FAILED(_producer.Configure(width, height, subtype));
    _producer.SetReceiver(&_receiver);
    _receiver.Start();

    // The Frame Server normally provides an allocator (IMFSampleAllocatorControl); fall back to our own.
    if (!_allocator) {
        RETURN_IF_FAILED(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&_allocator)));
        _ownAllocator = true;
    }
    RETURN_IF_FAILED(_allocator->InitializeSampleAllocator(10, current.Get()));

    RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
    _state = MF_STREAM_STATE_RUNNING;
    StartWorker();
    Trace(L"Stream started %ux%u %s", width, height, subtype == MFVideoFormat_NV12 ? L"NV12" : L"RGB32");
    return S_OK;
}

HRESULT MediaStream::Stop()
{
    StopWorker();
    _receiver.Stop();
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    if (_allocator) _allocator->UninitializeSampleAllocator();
    RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
    _state = MF_STREAM_STATE_STOPPED;
    Trace(L"Stream stopped");
    return S_OK;
}

void MediaStream::Shutdown()
{
    StopWorker();
    _receiver.Stop();
    std::lock_guard lock(_lock);
    if (_queue) {
        _queue->Shutdown();
        _queue.Reset();
    }
    if (_allocator) _allocator->UninitializeSampleAllocator();
    _allocator.Reset();
    _descriptor.Reset();
    _source.Reset();
}

// IMFMediaEventGenerator

STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->BeginGetEvent(callback, state);
}

STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->EndGetEvent(result, event);
}

STDMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    // Not under the lock: a blocking GetEvent must not stall RequestSample.
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard lock(_lock);
        queue = _queue;
    }
    RETURN_HR_IF(MF_E_SHUTDOWN, !queue);
    return queue->GetEvent(flags, event);
}

STDMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value)
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);
    return _queue->QueueEventParamVar(type, extendedType, status, value);
}

// IMFMediaStream

STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source)
{
    RETURN_HR_IF(E_POINTER, !source);
    *source = nullptr;
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_source);
    return _source.CopyTo(source);
}

STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor)
{
    RETURN_HR_IF(E_POINTER, !descriptor);
    *descriptor = nullptr;
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_descriptor);
    return _descriptor.CopyTo(descriptor);
}

STDMETHODIMP MediaStream::RequestSample(IUnknown* token)
{
    std::lock_guard lock(_lock);
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator);
    RETURN_HR_IF(MF_E_MEDIA_SOURCE_WRONGSTATE, _state != MF_STREAM_STATE_RUNNING);
    _requests.emplace_back(token);
    _wake.notify_one();
    return S_OK;
}

// Called with _lock held.
HRESULT MediaStream::DeliverSample(IUnknown* token)
{
    RETURN_HR_IF(MF_E_SHUTDOWN, !_queue || !_allocator);
    ComPtr<IMFSample> sample;
    RETURN_IF_FAILED(_allocator->AllocateSample(&sample));
    RETURN_IF_FAILED(_producer.Fill(sample.Get()));
    RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
    RETURN_IF_FAILED(sample->SetSampleDuration(333333));
    if (token) RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, token));
    return _queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
}

// Called with _lock held.
void MediaStream::StartWorker()
{
    if (_worker.joinable()) return;
    _workerStop = false;
    _worker = std::thread([this] { WorkerLoop(); });
}

// Must be called WITHOUT _lock held (joins the worker, which takes the lock).
void MediaStream::StopWorker()
{
    {
        std::lock_guard lock(_lock);
        _workerStop = true;
        _requests.clear();
    }
    _wake.notify_all();
    if (_worker.joinable() && _worker.get_id() != std::this_thread::get_id()) _worker.join();
}

void MediaStream::WorkerLoop()
{
    constexpr LONGLONG period = 333333;  // 100-ns units: 30 fps
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    LONGLONG due = MFGetSystemTime();
    for (;;) {
        // Wait for the next frame slot.
        LONGLONG wait = due - MFGetSystemTime();
        if (wait > 0) {
            if (timer) {
                LARGE_INTEGER relative;
                relative.QuadPart = -wait;
                SetWaitableTimer(timer, &relative, 0, nullptr, nullptr, FALSE);
                WaitForSingleObject(timer, 100);
            } else {
                Sleep(static_cast<DWORD>(wait / 10000));
            }
        }
        LONGLONG now = MFGetSystemTime();
        due = (now - due > period) ? now + period : due + period;  // fell behind: resync instead of bursting

        std::unique_lock lock(_lock);
        // Idle until someone asks for a frame (or we're stopped).
        _wake.wait(lock, [this] { return _workerStop || !_requests.empty(); });
        if (_workerStop) break;
        ComPtr<IUnknown> token = std::move(_requests.front());
        _requests.pop_front();
        HRESULT hr = DeliverSample(token.Get());
        if (FAILED(hr)) Trace(L"DeliverSample failed 0x%08X", hr);
    }
    if (timer) CloseHandle(timer);
}

// IMFMediaStream2

STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE value)
{
    MF_STREAM_STATE current;
    {
        std::lock_guard lock(_lock);
        current = _state;
    }
    if (current == value) return S_OK;
    switch (value) {
    case MF_STREAM_STATE_PAUSED:
        if (current != MF_STREAM_STATE_RUNNING) return MF_E_INVALID_STATE_TRANSITION;
        {
            std::lock_guard lock(_lock);
            _state = value;
        }
        return S_OK;
    case MF_STREAM_STATE_RUNNING:
        return Start(nullptr);
    case MF_STREAM_STATE_STOPPED:
        return Stop();
    default:
        return MF_E_INVALID_STATE_TRANSITION;
    }
}

STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* value)
{
    RETURN_HR_IF(E_POINTER, !value);
    std::lock_guard lock(_lock);
    *value = _state;
    return S_OK;
}

// IKsControl: no camera controls exposed yet.

STDMETHODIMP MediaStream::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP MediaStream::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP MediaStream::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*)
{
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
