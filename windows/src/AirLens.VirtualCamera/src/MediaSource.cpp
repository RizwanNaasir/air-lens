#include "MediaSource.h"

#include "MediaStream.h"

MediaSource::MediaSource() = default;

MediaSource::~MediaSource() { Shutdown(); }

STDMETHODIMP MediaSource::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == __uuidof(IMFMediaEventGenerator) || riid == __uuidof(IMFMediaSource) ||
        riid == __uuidof(IMFMediaSourceEx)) {
        *ppv = static_cast<IMFMediaSourceEx*>(this);
    } else if (riid == __uuidof(IMFGetService)) {
        *ppv = static_cast<IMFGetService*>(this);
    } else if (riid == __uuidof(IKsControl)) {
        *ppv = static_cast<IKsControl*>(this);
    } else {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) MediaSource::Release() {
    const ULONG count = --m_ref;
    if (count == 0) delete this;
    return count;
}

HRESULT MediaSource::Initialize() {
    AIRLENS_RETURN_IF_FAILED(MFCreateAttributes(&m_attributes, 4));
    AIRLENS_RETURN_IF_FAILED(MFCreateEventQueue(&m_events));

    m_stream.Attach(new (std::nothrow) MediaStream(this, 0));
    if (!m_stream) return E_OUTOFMEMORY;
    AIRLENS_RETURN_IF_FAILED(m_stream->Initialize());

    ComPtr<IMFStreamDescriptor> descriptor;
    AIRLENS_RETURN_IF_FAILED(m_stream->GetStreamDescriptor(&descriptor));
    IMFStreamDescriptor* descriptors[] = {descriptor.Get()};
    AIRLENS_RETURN_IF_FAILED(MFCreatePresentationDescriptor(1, descriptors, &m_presentation));
    AIRLENS_RETURN_IF_FAILED(m_presentation->SelectStream(0));
    AirLensTrace("media source initialised (pid %lu)", GetCurrentProcessId());
    return S_OK;
}

STDMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics) {
    if (!characteristics) return E_POINTER;
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

STDMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) {
    if (!descriptor) return E_POINTER;
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    return m_presentation->Clone(descriptor);
}

STDMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) {
    UNREFERENCED_PARAMETER(startPosition);
    if (!descriptor) return E_INVALIDARG;
    if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;

    ComPtr<MediaStream> stream;
    ComPtr<IMFMediaType> mediaType;
    bool announce;
    {
        std::lock_guard<std::mutex> guard(m_lock);
        AIRLENS_RETURN_IF_FAILED(CheckShutdown());
        BOOL selected = FALSE;
        ComPtr<IMFStreamDescriptor> sd;
        AIRLENS_RETURN_IF_FAILED(descriptor->GetStreamDescriptorByIndex(0, &selected, &sd));
        if (!selected) return MF_E_INVALIDREQUEST;
        // The client picks a size on its copy of the descriptor; honour that type.
        ComPtr<IMFMediaTypeHandler> handler;
        AIRLENS_RETURN_IF_FAILED(sd->GetMediaTypeHandler(&handler));
        AIRLENS_RETURN_IF_FAILED(handler->GetCurrentMediaType(&mediaType));
        announce = !m_streamAnnounced;
        m_streamAnnounced = true;
        stream = m_stream;
    }

    ComPtr<IUnknown> streamUnknown;
    AIRLENS_RETURN_IF_FAILED(stream.As(&streamUnknown));
    AIRLENS_RETURN_IF_FAILED(m_events->QueueEventParamUnk(announce ? MENewStream : MEUpdatedStream, GUID_NULL, S_OK, streamUnknown.Get()));
    AIRLENS_RETURN_IF_FAILED(stream->Start(mediaType.Get()));

    PROPVARIANT time;
    PropVariantInit(&time);
    time.vt = VT_I8;
    time.hVal.QuadPart = MFGetSystemTime();
    return m_events->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
}

STDMETHODIMP MediaSource::Stop() {
    ComPtr<MediaStream> stream;
    {
        std::lock_guard<std::mutex> guard(m_lock);
        AIRLENS_RETURN_IF_FAILED(CheckShutdown());
        stream = m_stream;
    }
    AIRLENS_RETURN_IF_FAILED(stream->Stop());
    return m_events->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

STDMETHODIMP MediaSource::Pause() {
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    return MF_E_INVALID_STATE_TRANSITION;  // live camera: no pause
}

STDMETHODIMP MediaSource::Shutdown() {
    ComPtr<MediaStream> stream;
    {
        std::lock_guard<std::mutex> guard(m_lock);
        if (m_shutdown) return S_OK;
        m_shutdown = true;
        stream = std::move(m_stream);
    }
    if (stream) stream->Shutdown();
    if (m_events) m_events->Shutdown();
    m_presentation.Reset();
    m_d3dManager.Reset();
    AirLensTrace("media source shut down");
    return S_OK;
}

STDMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes) {
    if (!attributes) return E_POINTER;
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    return m_attributes.CopyTo(attributes);
}

STDMETHODIMP MediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) {
    if (!attributes) return E_POINTER;
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    if (streamId != 0 || !m_stream) return MF_E_INVALIDSTREAMNUMBER;
    *attributes = m_stream->Attributes();
    (*attributes)->AddRef();
    return S_OK;
}

STDMETHODIMP MediaSource::SetD3DManager(IUnknown* manager) {
    // Samples are system-memory NV12 in v1; keep the manager for a future GPU path.
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    m_d3dManager = manager;
    return S_OK;
}

STDMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* ppv) {
    if (ppv) *ppv = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

STDMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event) {
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard<std::mutex> guard(m_lock);
        AIRLENS_RETURN_IF_FAILED(CheckShutdown());
        queue = m_events;
    }
    return queue->GetEvent(flags, event);  // may block: never under our lock
}

STDMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) {
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    return m_events->BeginGetEvent(callback, state);
}

STDMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) {
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    return m_events->EndGetEvent(result, event);
}

STDMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value) {
    std::lock_guard<std::mutex> guard(m_lock);
    AIRLENS_RETURN_IF_FAILED(CheckShutdown());
    return m_events->QueueEventParamVar(type, extendedType, status, value);
}
