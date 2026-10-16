#pragma once
#include "Common.h"
#include "SharedFrameReader.h"

class MediaSource;

// The single video stream of the AirLens camera: NV12 at 1080p (default), 720p or 4K, 30 fps.
//
// RequestSample only queues the token; a worker thread waits up to one frame interval for the
// AirLens app to publish a new frame, builds the sample and queues MEMediaSample. That keeps
// Frame Server's calls non-blocking and paces delivery to the real frame rate instead of handing
// out duplicates as fast as they are asked for. Black frames are delivered while the app is not
// streaming, so client apps never stall.
class MediaStream final : public IMFMediaStream2, public IKsControl {
public:
    MediaStream(MediaSource* source, DWORD streamId);

    HRESULT Initialize();
    HRESULT Start(IMFMediaType* mediaType);
    HRESULT Stop();
    void Shutdown();
    IMFAttributes* Attributes() const { return m_attributes.Get(); }
    bool IsActive() const { return m_state == MF_STREAM_STATE_RUNNING; }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override { return ++m_ref; }
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
    STDMETHODIMP RequestSample(IUnknown* token) override;

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

    // IKsControl: no custom properties.
    STDMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }

private:
    ~MediaStream();
    HRESULT CreateMediaType(UINT32 width, UINT32 height, IMFMediaType** type);
    void WorkerLoop();
    HRESULT DeliverSample(IUnknown* token);

    std::atomic<ULONG> m_ref{1};
    ObjectCounter m_counter;
    std::mutex m_lock;
    std::condition_variable m_wake;
    // Weak by design: the source owns the stream and shuts it down (clearing this) before it is
    // destroyed, so a strong reference here would only create a cycle that leaks inside Frame Server.
    MediaSource* m_source;
    DWORD m_streamId;
    ComPtr<IMFAttributes> m_attributes;
    ComPtr<IMFMediaEventQueue> m_events;
    ComPtr<IMFStreamDescriptor> m_descriptor;
    MF_STREAM_STATE m_state = MF_STREAM_STATE_STOPPED;
    bool m_shutdown = false;
    std::deque<ComPtr<IUnknown>> m_pendingTokens;  // null entries mean "no token"
    std::thread m_worker;

    UINT32 m_width = 1920, m_height = 1080, m_fpsNum = 30, m_fpsDen = 1;
    SharedFrameReader m_reader;
    std::vector<BYTE> m_frame;  // last good frame, re-sent if the producer is momentarily late
    bool m_haveFrame = false;

    // Pacing diagnostics (worker thread, except m_requests).
    std::atomic<ULONGLONG> m_requests{0};
    ULONGLONG m_delivered = 0, m_liveFrames = 0, m_deliverMsTotal = 0, m_statsSince = 0;
};
