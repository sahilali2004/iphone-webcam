#include "Activator.h"

#include "MediaSource.h"

Activator::~Activator() = default;

STDMETHODIMP Activator::QueryInterface(REFIID riid, void** ppv)
{
    RETURN_HR_IF(E_POINTER, !ppv);
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == __uuidof(IMFAttributes) || riid == __uuidof(IMFActivate)) {
        *ppv = static_cast<IMFActivate*>(this);
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
}

HRESULT Activator::Initialize()
{
    RETURN_HR_IF(E_OUTOFMEMORY, !_attributes);
    RETURN_IF_FAILED(SetUINT32(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1));
    RETURN_IF_FAILED(SetGUID(MFT_TRANSFORM_CLSID_Attribute, CLSID_PhoneWebcamSource));
    return S_OK;
}

STDMETHODIMP Activator::ActivateObject(REFIID riid, void** ppv)
{
    RETURN_HR_IF(E_POINTER, !ppv);
    *ppv = nullptr;
    std::lock_guard lock(_lock);
    if (!_source) {
        // Created lazily so attributes the Frame Server set on the activator are copied into the source.
        ComPtr<MediaSource> source;
        source.Attach(new (std::nothrow) MediaSource());
        RETURN_HR_IF(E_OUTOFMEMORY, !source);
        RETURN_IF_FAILED(source->Initialize(this));
        _source = source;
    }
    return _source->QueryInterface(riid, ppv);
}

STDMETHODIMP Activator::ShutdownObject()
{
    std::lock_guard lock(_lock);
    if (_source) {
        _source->Shutdown();
        _source.Reset();
    }
    return S_OK;
}

STDMETHODIMP Activator::DetachObject()
{
    std::lock_guard lock(_lock);
    _source.Reset();
    return S_OK;
}
