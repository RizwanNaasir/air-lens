#pragma once
#include "Common.h"

class MediaStream;

// The AirLens camera as Frame Server sees it: a live source with one video stream.
//
// Frame Server (svchost, Local Service, session 0) creates it through Activator, starts it when the
// first app opens the camera, and shares the stream between every app that opens it at once.
class MediaSource final : public IMFMediaSourceEx, public IMFGetService, public IKsControl {
public:
    MediaSource();  // out of line: ComPtr<MediaStream> needs the complete type
    HRESULT Initialize();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override { return ++m_ref; }
    STDMETHODIMP_(ULONG) Release() override;

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* value) override;

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* characteristics) override;
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) override;
    STDMETHODIMP Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
    STDMETHODIMP Stop() override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
    STDMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override;
    STDMETHODIMP SetD3DManager(IUnknown* manager) override;

    // IMFGetService
    STDMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* ppv) override;

    // IKsControl: no custom properties.
    STDMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }

private:
    ~MediaSource();
    HRESULT CheckShutdown() const { return m_shutdown ? MF_E_SHUTDOWN : S_OK; }

    std::atomic<ULONG> m_ref{1};
    ObjectCounter m_counter;
    std::mutex m_lock;
    bool m_shutdown = false;
    bool m_streamAnnounced = false;
    ComPtr<IMFAttributes> m_attributes;
    ComPtr<IMFMediaEventQueue> m_events;
    ComPtr<IMFPresentationDescriptor> m_presentation;
    ComPtr<MediaStream> m_stream;
    ComPtr<IUnknown> m_d3dManager;
};
