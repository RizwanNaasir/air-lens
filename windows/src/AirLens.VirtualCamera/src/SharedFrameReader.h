// Consumer side of the AirLens shared-memory frame ring (see include/AirLensSharedFrame.h).
#pragma once
#include "Common.h"
#include "../include/AirLensSharedFrame.h"

class SharedFrameReader {
public:
    SharedFrameReader() = default;
    SharedFrameReader(const SharedFrameReader&) = delete;
    SharedFrameReader& operator=(const SharedFrameReader&) = delete;
    ~SharedFrameReader() { Close(); }

    // Creates (or opens) the Global\ section and event. The media source runs inside the Frame
    // Server service, which holds SeCreateGlobalPrivilege; the DACL lets interactive users (the
    // AirLens app) read and write. Idempotent. Returns false if neither create nor open worked.
    bool EnsureCreated();

    // Tells the producer a client is streaming and which size it selected.
    void Heartbeat(UINT32 requestedWidth, UINT32 requestedHeight);

    // Waits up to `timeoutMs` for the producer to publish a frame newer than the last one read.
    void WaitForFrame(DWORD timeoutMs);

    // Copies the newest frame into `dst` (contiguous NV12, stride = width), scaling if the producer's
    // size differs. Returns false (and leaves `dst` untouched) when no valid frame is available.
    bool ReadLatest(BYTE* dst, UINT32 width, UINT32 height);

    void Close();

private:
    void ScaleNv12(const BYTE* src, UINT32 sw, UINT32 sh, UINT32 strideY, UINT32 strideUV,
                   BYTE* dst, UINT32 dw, UINT32 dh);

    HANDLE m_mapping = nullptr;
    HANDLE m_event = nullptr;
    AirLensVCamHeader* m_header = nullptr;
    BYTE* m_base = nullptr;
    int64_t m_lastSequence = 0;
    // Nearest-neighbour column maps, rebuilt only when sizes change.
    std::vector<UINT32> m_mapY, m_mapUV;
    UINT32 m_mapSrcW = 0, m_mapDstW = 0;
};

// Fills an NV12 frame with black (limited range: Y=16, Cb=Cr=128).
void FillBlackNv12(BYTE* dst, UINT32 width, UINT32 height);
