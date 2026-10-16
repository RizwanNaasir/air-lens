#include "Activator.h"

#include "MediaSource.h"

// Releasing the activator is not a shutdown request: MFCreateVirtualCamera (and Frame Server) keep
// using the source after letting go of the activator. Only ShutdownObject / Shutdown end it.
Activator::~Activator() = default;

STDMETHODIMP Activator::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == __uuidof(IMFAttributes) || riid == __uuidof(IMFActivate)) {
        *ppv = static_cast<IMFActivate*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) Activator::Release() {
    const ULONG count = --m_ref;
    if (count == 0) delete this;
    return count;
}

STDMETHODIMP Activator::ActivateObject(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    std::lock_guard<std::mutex> guard(m_lock);
    if (!m_source) {
        ComPtr<MediaSource> source;
        source.Attach(new (std::nothrow) MediaSource());
        if (!source) return E_OUTOFMEMORY;
        AIRLENS_RETURN_IF_FAILED(source->Initialize());
        m_source = source;
    }
    return m_source->QueryInterface(riid, ppv);
}

STDMETHODIMP Activator::ShutdownObject() {
    ComPtr<IMFMediaSource> source;
    {
        std::lock_guard<std::mutex> guard(m_lock);
        source = std::move(m_source);
    }
    if (source) source->Shutdown();
    return S_OK;
}

STDMETHODIMP Activator::DetachObject() {
    std::lock_guard<std::mutex> guard(m_lock);
    m_source.Reset();
    return S_OK;
}
