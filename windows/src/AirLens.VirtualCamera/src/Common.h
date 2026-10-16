// Shared includes and helpers for the AirLens virtual camera media source.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <ks.h>
#include <ksmedia.h>
#include <devicetopology.h>  // IKsControl (MIDL interface with a uuid, so __uuidof works)
#include <wrl/client.h>

#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

// {7B1F5C2A-3E44-4C1B-9A57-2D7A1E0C9B11}: must match kSourceClsid in Registration.cpp.
inline constexpr GUID CLSID_AirLensVirtualCamera = {
    0x7b1f5c2a, 0x3e44, 0x4c1b, {0x9a, 0x57, 0x2d, 0x7a, 0x1e, 0x0c, 0x9b, 0x11}};
inline constexpr wchar_t kClsidString[] = L"{7B1F5C2A-3E44-4C1B-9A57-2D7A1E0C9B11}";

// PINNAME_VIDEO_CAPTURE (ksmedia.h), spelled out so no GUID library is needed.
inline constexpr GUID kPinNameVideoCapture = {
    0xfb6c4281, 0x0353, 0x11d1, {0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba}};

// Live COM objects + LockServer count, for DllCanUnloadNow.
extern std::atomic<long> g_objectCount;
extern HMODULE g_module;

// OutputDebugString plus %ProgramData%\AirLens\vcam.log (best effort; the source runs inside
// Frame Server, where a debugger is rarely attached).
void AirLensTrace(_Printf_format_string_ const char* format, ...);

#define AIRLENS_RETURN_IF_FAILED(expr)                                              \
    do {                                                                            \
        HRESULT hr__ = (expr);                                                      \
        if (FAILED(hr__)) {                                                         \
            AirLensTrace("%s failed: 0x%08X (%s:%d)", #expr, hr__, __FILE__, __LINE__); \
            return hr__;                                                            \
        }                                                                           \
    } while (0)

// Counts a COM object for DllCanUnloadNow for its whole lifetime.
struct ObjectCounter {
    ObjectCounter() { ++g_objectCount; }
    ~ObjectCounter() { --g_objectCount; }
};

// Forwards every IMFAttributes method to an inner attribute store.
template <class TBase>
class AttributesBase : public TBase {
protected:
    ComPtr<IMFAttributes> m_attributes;
    HRESULT InitAttributes(UINT32 initialSize = 8) { return MFCreateAttributes(&m_attributes, initialSize); }

public:
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return m_attributes->GetItem(k, v); }
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return m_attributes->GetItemType(k, t); }
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return m_attributes->CompareItem(k, v, r); }
    STDMETHODIMP Compare(IMFAttributes* a, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return m_attributes->Compare(a, m, r); }
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return m_attributes->GetUINT32(k, v); }
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return m_attributes->GetUINT64(k, v); }
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return m_attributes->GetDouble(k, v); }
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return m_attributes->GetGUID(k, v); }
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* l) override { return m_attributes->GetStringLength(k, l); }
    STDMETHODIMP GetString(REFGUID k, LPWSTR s, UINT32 c, UINT32* l) override { return m_attributes->GetString(k, s, c, l); }
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* s, UINT32* l) override { return m_attributes->GetAllocatedString(k, s, l); }
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* s) override { return m_attributes->GetBlobSize(k, s); }
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 c, UINT32* s) override { return m_attributes->GetBlob(k, b, c, s); }
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* s) override { return m_attributes->GetAllocatedBlob(k, b, s); }
    STDMETHODIMP GetUnknown(REFGUID k, REFIID i, LPVOID* p) override { return m_attributes->GetUnknown(k, i, p); }
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return m_attributes->SetItem(k, v); }
    STDMETHODIMP DeleteItem(REFGUID k) override { return m_attributes->DeleteItem(k); }
    STDMETHODIMP DeleteAllItems() override { return m_attributes->DeleteAllItems(); }
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return m_attributes->SetUINT32(k, v); }
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return m_attributes->SetUINT64(k, v); }
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return m_attributes->SetDouble(k, v); }
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return m_attributes->SetGUID(k, v); }
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return m_attributes->SetString(k, v); }
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 c) override { return m_attributes->SetBlob(k, b, c); }
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* u) override { return m_attributes->SetUnknown(k, u); }
    STDMETHODIMP LockStore() override { return m_attributes->LockStore(); }
    STDMETHODIMP UnlockStore() override { return m_attributes->UnlockStore(); }
    STDMETHODIMP GetCount(UINT32* c) override { return m_attributes->GetCount(c); }
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return m_attributes->GetItemByIndex(i, k, v); }
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return m_attributes->CopyAllItems(d); }
};
