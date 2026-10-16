#include "SharedFrameReader.h"

#include <sddl.h>

#pragma comment(lib, "advapi32.lib")

namespace {

// DACL: SYSTEM, Local Service and Administrators get full access; interactive and authenticated
// users (the AirLens desktop app) get read + write + execute. For the section that is
// FILE_MAP_READ|WRITE (|EXECUTE, unused); for the event, EVENT_MODIFY_STATE comes from GW and
// SYNCHRONIZE from GX, which is why GX is needed for OpenEvent(SYNCHRONIZE|EVENT_MODIFY_STATE).
// SACL: a *medium* mandatory label. Objects created by a service otherwise carry its System
// integrity label with no-write-up, which silently blocks the (medium-integrity) app from writing
// although the DACL allows it. Medium still keeps low-integrity sandboxed processes out.
constexpr wchar_t kSddl[] = L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGWGX;;;IU)(A;;GRGWGX;;;AU)S:(ML;;NW;;;ME)";

struct SecurityAttributes {
    SECURITY_ATTRIBUTES sa{sizeof(SECURITY_ATTRIBUTES), nullptr, FALSE};
    PSECURITY_DESCRIPTOR sd = nullptr;
    SecurityAttributes() {
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(kSddl, SDDL_REVISION_1, &sd, nullptr)) {
            sa.lpSecurityDescriptor = sd;
        }
    }
    ~SecurityAttributes() {
        if (sd) LocalFree(sd);
    }
};

}  // namespace

void FillBlackNv12(BYTE* dst, UINT32 width, UINT32 height) {
    const size_t luma = static_cast<size_t>(width) * height;
    memset(dst, 16, luma);
    memset(dst + luma, 128, luma / 2);
}

bool SharedFrameReader::EnsureCreated() {
    if (m_header) return true;
    SecurityAttributes security;
    const ULONGLONG size = AIRLENS_VCAM_MAPPING_SIZE;
    m_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &security.sa, PAGE_READWRITE,
                                   static_cast<DWORD>(size >> 32), static_cast<DWORD>(size & 0xFFFFFFFF),
                                   AIRLENS_VCAM_MAPPING_NAME);
    const bool created = m_mapping && GetLastError() != ERROR_ALREADY_EXISTS;
    if (!m_mapping) {
        // Not in a service (e.g. loaded by an app enumerating cameras): try opening instead.
        m_mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, AIRLENS_VCAM_MAPPING_NAME);
        if (!m_mapping) {
            AirLensTrace("shared memory unavailable (error %lu)", GetLastError());
            return false;
        }
    }
    m_base = static_cast<BYTE*>(MapViewOfFile(m_mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0));
    if (!m_base) {
        AirLensTrace("MapViewOfFile failed (error %lu)", GetLastError());
        Close();
        return false;
    }
    m_header = reinterpret_cast<AirLensVCamHeader*>(m_base);
    if (created || m_header->magic != AIRLENS_VCAM_MAGIC) {
        ZeroMemory(m_header, AIRLENS_VCAM_HEADER_SIZE);
        m_header->version = AIRLENS_VCAM_VERSION;
        m_header->headerSize = AIRLENS_VCAM_HEADER_SIZE;
        m_header->slotCount = AIRLENS_VCAM_SLOT_COUNT;
        m_header->maxWidth = AIRLENS_VCAM_MAX_WIDTH;
        m_header->maxHeight = AIRLENS_VCAM_MAX_HEIGHT;
        m_header->slotStride = AIRLENS_VCAM_SLOT_STRIDE;
        MemoryBarrier();
        m_header->magic = AIRLENS_VCAM_MAGIC;  // last: the app validates it before writing
    }
    m_event = CreateEventW(&security.sa, FALSE, FALSE, AIRLENS_VCAM_EVENT_NAME);
    if (!m_event) m_event = OpenEventW(SYNCHRONIZE, FALSE, AIRLENS_VCAM_EVENT_NAME);
    m_lastSequence = 0;
    AirLensTrace("shared memory %s (%llu bytes)", created ? "created" : "opened", size);
    return true;
}

void SharedFrameReader::Heartbeat(UINT32 requestedWidth, UINT32 requestedHeight) {
    if (!m_header) return;
    FILETIME now;
    GetSystemTimeAsFileTime(&now);
    m_header->requestedWidth = requestedWidth;
    m_header->requestedHeight = requestedHeight;
    const int64_t value = (static_cast<int64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
    InterlockedExchange64(&m_header->consumerHeartbeat100ns, value);
}

void SharedFrameReader::WaitForFrame(DWORD timeoutMs) {
    if (!m_header) {
        Sleep(timeoutMs);
        return;
    }
    if (InterlockedCompareExchange64(&m_header->writeSequence, 0, 0) != m_lastSequence) return;  // already new
    if (m_event) WaitForSingleObject(m_event, timeoutMs);
    else Sleep(timeoutMs);
}

bool SharedFrameReader::ReadLatest(BYTE* dst, UINT32 width, UINT32 height) {
    if (!m_header || m_header->magic != AIRLENS_VCAM_MAGIC) return false;
    for (int attempt = 0; attempt < 2; ++attempt) {
        const int64_t n = InterlockedCompareExchange64(&m_header->writeSequence, 0, 0);
        if (n <= 0) return false;
        const UINT32 index = static_cast<UINT32>(n % AIRLENS_VCAM_SLOT_COUNT);
        AirLensVCamSlot* slot = &m_header->slots[index];
        if (InterlockedCompareExchange64(&slot->sequence, 0, 0) != n) continue;  // being rewritten
        const UINT32 sw = slot->width, sh = slot->height;
        if (slot->format != AIRLENS_VCAM_FORMAT_NV12 || sw == 0 || sh == 0 || sw > AIRLENS_VCAM_MAX_WIDTH ||
            sh > AIRLENS_VCAM_MAX_HEIGHT || slot->strideY < sw || slot->strideUV < sw) {
            return false;
        }
        const BYTE* src = m_base + AIRLENS_VCAM_HEADER_SIZE + static_cast<size_t>(index) * AIRLENS_VCAM_SLOT_STRIDE;
        ScaleNv12(src, sw, sh, slot->strideY, slot->strideUV, dst, width, height);
        MemoryBarrier();
        // Seqlock check: if the producer lapped us during the copy, the frame may be torn.
        if (InterlockedCompareExchange64(&slot->sequence, 0, 0) == n) {
            m_lastSequence = n;
            return true;
        }
    }
    return false;
}

void SharedFrameReader::ScaleNv12(const BYTE* src, UINT32 sw, UINT32 sh, UINT32 strideY, UINT32 strideUV,
                                  BYTE* dst, UINT32 dw, UINT32 dh) {
    const BYTE* srcUV = src + static_cast<size_t>(strideY) * sh;
    BYTE* dstUV = dst + static_cast<size_t>(dw) * dh;

    if (sw == dw && sh == dh) {
        for (UINT32 y = 0; y < dh; ++y) memcpy(dst + static_cast<size_t>(y) * dw, src + static_cast<size_t>(y) * strideY, dw);
        for (UINT32 y = 0; y < dh / 2; ++y) memcpy(dstUV + static_cast<size_t>(y) * dw, srcUV + static_cast<size_t>(y) * strideUV, dw);
        return;
    }

    if (sw == dw * 2 && sh == dh * 2) {
        // Exact 2:1 (e.g. 4K phone stream -> 1080p client): 2x2 box filter, no aliasing.
        for (UINT32 y = 0; y < dh; ++y) {
            const BYTE* r0 = src + static_cast<size_t>(2 * y) * strideY;
            const BYTE* r1 = r0 + strideY;
            BYTE* o = dst + static_cast<size_t>(y) * dw;
            for (UINT32 x = 0; x < dw; ++x) o[x] = static_cast<BYTE>((r0[2 * x] + r0[2 * x + 1] + r1[2 * x] + r1[2 * x + 1] + 2) >> 2);
        }
        for (UINT32 y = 0; y < dh / 2; ++y) {
            const BYTE* r0 = srcUV + static_cast<size_t>(2 * y) * strideUV;
            const BYTE* r1 = r0 + strideUV;
            BYTE* o = dstUV + static_cast<size_t>(y) * dw;
            for (UINT32 x = 0; x < dw / 2; ++x) {
                const UINT32 s = 4 * x;  // two interleaved CbCr pairs per output pair
                o[2 * x] = static_cast<BYTE>((r0[s] + r0[s + 2] + r1[s] + r1[s + 2] + 2) >> 2);
                o[2 * x + 1] = static_cast<BYTE>((r0[s + 1] + r0[s + 3] + r1[s + 1] + r1[s + 3] + 2) >> 2);
            }
        }
        return;
    }

    // Any other size: keep the picture's aspect ratio. Fit it inside the requested frame (black
    // pillar- or letterbox bars) rather than stretch it: a 4:3 phone stream in a 16:9 call must not
    // make faces wide. Nearest neighbour with cached column maps.
    UINT32 fitW = dw, fitH = dh;
    if (static_cast<UINT64>(sw) * dh > static_cast<UINT64>(dw) * sh) {
        fitH = static_cast<UINT32>(static_cast<UINT64>(dw) * sh / sw) & ~1u;
    } else {
        fitW = static_cast<UINT32>(static_cast<UINT64>(dh) * sw / sh) & ~1u;
    }
    if (fitW < 2 || fitH < 2) return;
    const UINT32 offX = ((dw - fitW) / 2) & ~1u;
    const UINT32 offY = ((dh - fitH) / 2) & ~1u;
    if (fitW != dw || fitH != dh) FillBlackNv12(dst, dw, dh);

    if (m_mapSrcW != sw || m_mapDstW != fitW) {
        m_mapY.resize(fitW);
        m_mapUV.resize(fitW / 2);
        for (UINT32 x = 0; x < fitW; ++x) m_mapY[x] = static_cast<UINT32>(static_cast<UINT64>(x) * sw / fitW);
        for (UINT32 x = 0; x < fitW / 2; ++x) m_mapUV[x] = 2 * static_cast<UINT32>(static_cast<UINT64>(x) * (sw / 2) / (fitW / 2));
        m_mapSrcW = sw;
        m_mapDstW = fitW;
    }
    for (UINT32 y = 0; y < fitH; ++y) {
        const BYTE* row = src + static_cast<size_t>(static_cast<UINT64>(y) * sh / fitH) * strideY;
        BYTE* o = dst + static_cast<size_t>(offY + y) * dw + offX;
        for (UINT32 x = 0; x < fitW; ++x) o[x] = row[m_mapY[x]];
    }
    for (UINT32 y = 0; y < fitH / 2; ++y) {
        const BYTE* row = srcUV + static_cast<size_t>(static_cast<UINT64>(y) * (sh / 2) / (fitH / 2)) * strideUV;
        BYTE* o = dstUV + static_cast<size_t>(offY / 2 + y) * dw + offX;
        for (UINT32 x = 0; x < fitW / 2; ++x) {
            o[2 * x] = row[m_mapUV[x]];
            o[2 * x + 1] = row[m_mapUV[x] + 1];
        }
    }
}

void SharedFrameReader::Close() {
    if (m_base) UnmapViewOfFile(m_base);
    m_base = nullptr;
    m_header = nullptr;
    if (m_mapping) CloseHandle(m_mapping);
    m_mapping = nullptr;
    if (m_event) CloseHandle(m_event);
    m_event = nullptr;
}
