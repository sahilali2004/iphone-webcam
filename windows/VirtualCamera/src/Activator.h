#pragma once

#include "Common.h"

#include "MediaSource.h"

/// What the Frame Server CoCreates from our CLSID. Hands out the (single) media source.
class Activator final : public AttributesImpl<IMFActivate> {
    IMPLEMENT_REFCOUNT

public:
    ~Activator();

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    STDMETHODIMP ShutdownObject() override;
    STDMETHODIMP DetachObject() override;

    HRESULT Initialize();

private:
    std::mutex _lock;
    ComPtr<MediaSource> _source;
};
