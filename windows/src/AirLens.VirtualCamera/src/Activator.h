#pragma once
#include "Common.h"

class MediaSource;

// The COM class behind CLSID_AirLensVirtualCamera. Frame Server creates it and calls
// ActivateObject(IID_IMFMediaSource) to get the actual source; ShutdownObject ends it.
class Activator final : public AttributesBase<IMFActivate> {
public:
    HRESULT Initialize() { return InitAttributes(); }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override { return ++m_ref; }
    STDMETHODIMP_(ULONG) Release() override;

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    STDMETHODIMP ShutdownObject() override;
    STDMETHODIMP DetachObject() override;

private:
    ~Activator();

    std::atomic<ULONG> m_ref{1};
    ObjectCounter m_counter;
    std::mutex m_lock;
    ComPtr<IMFMediaSource> m_source;
};
