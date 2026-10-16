// AirLens virtual camera <-> app shared-memory contract.
// Mirrored byte-for-byte by windows/src/AirLens.Core/VirtualCamera/SharedFrameLayout.cs.
//
// Who creates what (see docs/ARCHITECTURE.md §3.1):
//   * The media source runs inside the Frame Server service (session 0, Local Service). It CREATES
//     the section and event, because creating Global\ objects needs SeCreateGlobalPrivilege, which
//     services have and standard users do not, and applies a DACL granting the interactive user
//     read/write.
//   * The AirLens app OPENS them, retrying until a camera consumer exists. While no app is using the
//     camera the section does not exist and the app skips the GPU->CPU readback entirely.
//
// Publication protocol (single producer, single consumer, lock-free):
//   producer: n = writeSequence + 1; slot = n % SlotCount; slot.sequence = 0 (busy);
//             copy NV12; fill slot fields; slot.sequence = n; writeSequence = n (release); SetEvent.
//   consumer: n = writeSequence (acquire); slot = n % SlotCount; copy; if slot.sequence != n the
//             producer lapped us mid-copy: discard and retry. With 3 slots the producer must lap
//             twice, which takes two frame intervals; a 4K NV12 copy takes ~2 ms.
#pragma once
#include <stddef.h>
#include <stdint.h>

#define AIRLENS_VCAM_MAPPING_NAME L"Global\\AirLensVirtualCamFrames"
#define AIRLENS_VCAM_EVENT_NAME   L"Global\\AirLensVirtualCamFrameReady"
#define AIRLENS_VCAM_MAGIC        0x43564C41u /* 'ALVC' little-endian */
#define AIRLENS_VCAM_VERSION      1u
#define AIRLENS_VCAM_SLOT_COUNT   3u
#define AIRLENS_VCAM_MAX_WIDTH    3840u
#define AIRLENS_VCAM_MAX_HEIGHT   2160u
#define AIRLENS_VCAM_HEADER_SIZE  4096u
#define AIRLENS_VCAM_FORMAT_NV12  1u

// NV12 = width*height luma + width*height/2 interleaved chroma, rounded up to 4 KiB.
#define AIRLENS_VCAM_SLOT_STRIDE  ((((AIRLENS_VCAM_MAX_WIDTH * AIRLENS_VCAM_MAX_HEIGHT * 3u) / 2u) + 4095u) & ~4095u)
#define AIRLENS_VCAM_MAPPING_SIZE (AIRLENS_VCAM_HEADER_SIZE + AIRLENS_VCAM_SLOT_COUNT * AIRLENS_VCAM_SLOT_STRIDE)

#pragma pack(push, 8)
typedef struct AirLensVCamSlot {
    volatile int64_t sequence;     // 0 while being written; publication number when complete
    int64_t timestamp100ns;        // QueryPerformanceCounter converted to 100 ns units
    uint32_t width;
    uint32_t height;
    uint32_t strideY;              // bytes per luma row
    uint32_t strideUV;             // bytes per chroma row (interleaved CbCr)
    uint32_t format;               // AIRLENS_VCAM_FORMAT_NV12
    uint32_t flags;                // bit0: full range
    uint8_t reserved[24];
} AirLensVCamSlot;                 // 64 bytes

typedef struct AirLensVCamHeader {
    uint32_t magic;                // AIRLENS_VCAM_MAGIC, written by the media source
    uint32_t version;
    uint32_t headerSize;
    uint32_t slotCount;
    uint32_t maxWidth;
    uint32_t maxHeight;
    uint32_t slotStride;
    uint32_t reserved0;

    // Written by the media source.
    volatile int64_t consumerHeartbeat100ns; // FILETIME (UTC) refreshed ~4x/s while a client streams
    uint32_t requestedWidth;                 // media type the client selected (0 = any)
    uint32_t requestedHeight;

    // Written by the app.
    uint32_t producerWidth;                  // native stream size, used to build the media type list
    uint32_t producerHeight;
    uint32_t producerFpsNumerator;
    uint32_t producerFpsDenominator;
    volatile int64_t writeSequence;          // last published publication number

    uint8_t reserved1[56];
    AirLensVCamSlot slots[AIRLENS_VCAM_SLOT_COUNT];
} AirLensVCamHeader;
#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(AirLensVCamSlot) == 64, "slot layout is part of the contract");
static_assert(offsetof(AirLensVCamHeader, consumerHeartbeat100ns) == 32, "header layout is part of the contract");
static_assert(offsetof(AirLensVCamHeader, writeSequence) == 64, "header layout is part of the contract");
static_assert(offsetof(AirLensVCamHeader, slots) == 128, "header layout is part of the contract");
static_assert(sizeof(AirLensVCamHeader) <= AIRLENS_VCAM_HEADER_SIZE, "header must fit its page");
#endif
