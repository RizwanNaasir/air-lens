#include "MediaStream.h"

#include "MediaSource.h"

namespace {
struct Size {
    UINT32 width, height;
};
// Order matters: the first type is the default most apps open with.
constexpr Size kSizes[] = {{1920, 1080}, {1280, 720}, {3840, 2160}};
constexpr UINT32 kFps = 30;
}  // namespace

MediaStream::MediaStream(MediaSource* source, DWORD streamId) : m_source(source), m_streamId(streamId) {}

MediaStream::~MediaStream() { Shutdown(); }

STDMETHODIMP MediaStream::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == __uuidof(IMFMediaEventGenerator) || riid == __uuidof(IMFMediaStream) ||
        riid == __uuidof(IMFMediaStream2)) {
        *ppv = static_cast<IMFMediaStream2*>(this);
    } else if (riid == __uuidof(IKsControl)) {
        *ppv = static_cast<IKsControl*>(this);
    } else {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) MediaStream::Release() {
    const ULONG count = --m_ref;
    if (count == 0) delete this;
    return count;
}

HRESULT MediaStream::CreateMediaType(UINT32 width, UINT32 height, IMFMediaType** result) {
    ComPtr<IMFMediaType> type;
    AIRLENS_RETURN_IF_FAILED(MFCreateMediaType(&type));
    AIRLENS_RETURN_IF_FAILED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    AIRLENS_RETURN_IF_FAILED(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
    AIRLENS_RETURN_IF_FAILED(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height));
    AIRLENS_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, kFps, 1));
    AIRLENS_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_DEFAULT_STRIDE, width));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_SAMPLE_SIZE, width * height * 3 / 2));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_AVG_BITRATE, width * height * 12 * kFps));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
    AIRLENS_RETURN_IF_FAILED(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
    *result = type.Detach();
    return S_OK;
}

HRESULT MediaStream::Initialize() {
    AIRLENS_RETURN_IF_FAILED(MFCreateAttributes(&m_attributes, 8));
    AIRLENS_RETURN_IF_FAILED(m_attributes->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, kPinNameVideoCapture));
    AIRLENS_RETURN_IF_FAILED(m_attributes->SetUINT32(MF_DEVICESTREAM_STREAM_ID, m_streamId));
    AIRLENS_RETURN_IF_FAILED(m_attributes->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
    AIRLENS_RETURN_IF_FAILED(m_attributes->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
    AIRLENS_RETURN_IF_FAILED(MFCreateEventQueue(&m_events));

    ComPtr<IMFMediaType> types[ARRAYSIZE(kSizes)];
    IMFMediaType* raw[ARRAYSIZE(kSizes)] = {};
    for (size_t i = 0; i < ARRAYSIZE(kSizes); ++i) {
        AIRLENS_RETURN_IF_FAILED(CreateMediaType(kSizes[i].width, kSizes[i].height, &types[i]));
        raw[i] = types[i].Get();
    }
    AIRLENS_RETURN_IF_FAILED(MFCreateStreamDescriptor(m_streamId, ARRAYSIZE(kSizes), raw, &m_descriptor));
    ComPtr<IMFMediaTypeHandler> handler;
    AIRLENS_RETURN_IF_FAILED(m_descriptor->GetMediaTypeHandler(&handler));
    AIRLENS_RETURN_IF_FAILED(handler->SetCurrentMediaType(raw[0]));
    // The descriptor carries the same identity attributes as the stream.
    AIRLENS_RETURN_IF_FAILED(m_attributes->CopyAllItems(m_descriptor.Get()));
    return S_OK;
}

HRESULT MediaStream::Start(IMFMediaType* mediaType) {
    {
        std::lock_guard<std::mutex> guard(m_lock);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (mediaType) {
            UINT32 w = 0, h = 0, num = 0, den = 0;
            if (SUCCEEDED(MFGetAttributeSize(mediaType, MF_MT_FRAME_SIZE, &w, &h)) && w && h) {
                m_width = w;
                m_height = h;
            }
            if (SUCCEEDED(MFGetAttributeRatio(mediaType, MF_MT_FRAME_RATE, &num, &den)) && num && den) {
                m_fpsNum = num;
                m_fpsDen = den;
            }
        }
        m_frame.assign(static_cast<size_t>(m_width) * m_height * 3 / 2, 0);
        FillBlackNv12(m_frame.data(), m_width, m_height);
        m_haveFrame = false;
        m_state = MF_STREAM_STATE_RUNNING;
        if (!m_worker.joinable()) m_worker = std::thread(&MediaStream::WorkerLoop, this);
    }
    AirLensTrace("stream start %ux%u @ %u/%u", m_width, m_height, m_fpsNum, m_fpsDen);
    return m_events->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
}

HRESULT MediaStream::Stop() {
    {
        std::lock_guard<std::mutex> guard(m_lock);
        if (m_shutdown) return MF_E_SHUTDOWN;
        m_state = MF_STREAM_STATE_STOPPED;
        m_pendingTokens.clear();
    }
    return m_events->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
}

void MediaStream::Shutdown() {
    std::thread worker;
    {
        std::lock_guard<std::mutex> guard(m_lock);
        if (m_shutdown) return;
        m_shutdown = true;
        m_state = MF_STREAM_STATE_STOPPED;
        m_pendingTokens.clear();
        worker = std::move(m_worker);
    }
    m_wake.notify_all();
    if (worker.joinable()) worker.join();
    if (m_events) m_events->Shutdown();
    m_reader.Close();
    std::lock_guard<std::mutex> guard(m_lock);
    m_source = nullptr;
}

STDMETHODIMP MediaStream::RequestSample(IUnknown* token) {
    std::lock_guard<std::mutex> guard(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    if (m_state != MF_STREAM_STATE_RUNNING) return MF_E_MEDIA_SOURCE_WRONGSTATE;
    ++m_requests;
    m_pendingTokens.emplace_back(token);
    m_wake.notify_one();
    return S_OK;
}

void MediaStream::WorkerLoop() {
    // Frame Server runs with COM initialised on its threads; ours needs MTA for the MF calls.
    const HRESULT coInit = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    for (;;) {
        ComPtr<IUnknown> token;
        {
            std::unique_lock<std::mutex> lock(m_lock);
            m_wake.wait(lock, [this] { return m_shutdown || !m_pendingTokens.empty(); });
            if (m_shutdown) break;
            token = std::move(m_pendingTokens.front());
            m_pendingTokens.pop_front();
        }
        const ULONGLONG begin = GetTickCount64();
        const HRESULT hr = DeliverSample(token.Get());
        m_deliverMsTotal += GetTickCount64() - begin;
        ++m_delivered;
        if (FAILED(hr) && hr != MF_E_SHUTDOWN) {
            AirLensTrace("DeliverSample failed: 0x%08X", hr);
            m_events->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr);
        }
        // Pacing diagnostics every 2 s: who is slow, Frame Server asking or us delivering?
        const ULONGLONG now = GetTickCount64();
        if (now - m_statsSince >= 2000) {
            AirLensTrace("stats: %llu requests, %llu delivered (%llu with a live frame), avg deliver %.1f ms",
                         m_requests.exchange(0), m_delivered, m_liveFrames,
                         m_delivered ? static_cast<double>(m_deliverMsTotal) / m_delivered : 0.0);
            m_delivered = m_liveFrames = m_deliverMsTotal = 0;
            m_statsSince = now;
        }
    }
    if (SUCCEEDED(coInit)) CoUninitialize();
}

HRESULT MediaStream::DeliverSample(IUnknown* token) {
    const DWORD frameMs = m_fpsNum ? (1000 * m_fpsDen + m_fpsNum - 1) / m_fpsNum : 33;
    if (m_reader.EnsureCreated()) {
        m_reader.Heartbeat(m_width, m_height);
        m_reader.WaitForFrame(frameMs);
        if (m_reader.ReadLatest(m_frame.data(), m_width, m_height)) {
            m_haveFrame = true;
            ++m_liveFrames;
        }
    } else {
        Sleep(frameMs);  // pace black frames when the section cannot be created
    }

    const DWORD size = static_cast<DWORD>(m_frame.size());
    ComPtr<IMFMediaBuffer> buffer;
    AIRLENS_RETURN_IF_FAILED(MFCreateMemoryBuffer(size, &buffer));
    BYTE* data = nullptr;
    AIRLENS_RETURN_IF_FAILED(buffer->Lock(&data, nullptr, nullptr));
    memcpy(data, m_frame.data(), size);
    buffer->Unlock();
    AIRLENS_RETURN_IF_FAILED(buffer->SetCurrentLength(size));

    ComPtr<IMFSample> sample;
    AIRLENS_RETURN_IF_FAILED(MFCreateSample(&sample));
    AIRLENS_RETURN_IF_FAILED(sample->AddBuffer(buffer.Get()));
    AIRLENS_RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
    AIRLENS_RETURN_IF_FAILED(sample->SetSampleDuration(10'000'000LL * m_fpsDen / (m_fpsNum ? m_fpsNum : 30)));
    if (token) AIRLENS_RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, token));

    {
        std::lock_guard<std::mutex> guard(m_lock);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (m_state != MF_STREAM_STATE_RUNNING) return S_OK;  // stopped while we waited: drop
    }
    return m_events->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
}

STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE state) {
    std::lock_guard<std::mutex> guard(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    if (state == MF_STREAM_STATE_PAUSED) return MF_E_INVALID_STATE_TRANSITION;
    m_state = state;
    if (state != MF_STREAM_STATE_RUNNING) m_pendingTokens.clear();
    return S_OK;
}

STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* state) {
    if (!state) return E_POINTER;
    std::lock_guard<std::mutex> guard(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    *state = m_state;
    return S_OK;
}

STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source) {
    if (!source) return E_POINTER;
    std::lock_guard<std::mutex> guard(m_lock);
    if (m_shutdown || !m_source) return MF_E_SHUTDOWN;
    *source = static_cast<IMFMediaSourceEx*>(m_source);
    (*source)->AddRef();
    return S_OK;
}

STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor) {
    if (!descriptor) return E_POINTER;
    std::lock_guard<std::mutex> guard(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    return m_descriptor.CopyTo(descriptor);
}

STDMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event) {
    ComPtr<IMFMediaEventQueue> queue;
    {
        std::lock_guard<std::mutex> guard(m_lock);
        if (m_shutdown) return MF_E_SHUTDOWN;
        queue = m_events;
    }
    return queue->GetEvent(flags, event);  // may block: never under our lock
}

STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) {
    std::lock_guard<std::mutex> guard(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    return m_events->BeginGetEvent(callback, state);
}

STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) {
    std::lock_guard<std::mutex> guard(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    return m_events->EndGetEvent(result, event);
}

STDMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value) {
    std::lock_guard<std::mutex> guard(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    return m_events->QueueEventParamVar(type, extendedType, status, value);
}
