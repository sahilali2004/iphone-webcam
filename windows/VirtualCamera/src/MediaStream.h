#pragma once

#include "Common.h"
#include "FrameProducer.h"

#include <condition_variable>
#include <deque>
#include <thread>

/// The single video stream of the virtual camera. Samples come from the Frame Server's allocator;
/// pixels from FrameProducer. Interface contract follows Microsoft's virtual-camera samples
/// and smourier/VCamSample (MIT).
class MediaStream final : public AttributesImpl<IMFAttributes>, public IMFMediaStream2, public IKsControl {
    IMPLEMENT_REFCOUNT

public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;

    // IMFMediaEventGenerator
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
    STDMETHODIMP RequestSample(IUnknown* token) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE value) override;
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* value) override;

    // IKsControl
    STDMETHODIMP KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
    STDMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
    STDMETHODIMP KsEvent(PKSEVENT event, ULONG eventLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;

    HRESULT Initialize(IMFMediaSource* source, DWORD id);
    HRESULT Start(IMFMediaType* type);
    HRESULT Stop();
    void Shutdown();
    HRESULT SetAllocator(IUnknown* allocator);

private:
    // Delivery thread: answers RequestSample on a 30 fps clock, like a real camera
    // (answering immediately would let a greedy reader pull hundreds of frames per second).
    void StartWorker();
    void StopWorker();
    void WorkerLoop();
    HRESULT DeliverSample(IUnknown* token);

    std::mutex _lock;
    std::condition_variable _wake;
    std::thread _worker;
    bool _workerStop = false;
    std::deque<ComPtr<IUnknown>> _requests;  // tokens (may be null), one per RequestSample
    MF_STREAM_STATE _state = MF_STREAM_STATE_STOPPED;
    DWORD _id = 0;
    ComPtr<IMFMediaSource> _source;  // cycle with the source; broken in Shutdown()
    ComPtr<IMFStreamDescriptor> _descriptor;
    ComPtr<IMFMediaEventQueue> _queue;
    ComPtr<IMFVideoSampleAllocatorEx> _allocator;
    bool _ownAllocator = false;
    FrameReceiver _receiver;  // frames from the companion app (declared before _producer, which points at it)
    FrameProducer _producer;
};
