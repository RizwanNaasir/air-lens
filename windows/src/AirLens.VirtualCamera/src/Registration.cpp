// Starts and stops the AirLens Media Foundation virtual camera for the current user.
//
// Exported for the AirLens app (P/Invoke from AirLens.Core/VirtualCamera/VirtualCameraHost.cs).
// The same DLL is also the in-proc COM server of the media source that Windows' Frame Server loads
// (MediaSource.cpp, phase 2); this file only manages the camera's registration and lifetime.
//
// No elevation here: MFVirtualCameraAccess_CurrentUser + MFVirtualCameraLifetime_Session need
// none. The one-time elevated step is the installer registering the source CLSID under HKLM
// (see docs/ARCHITECTURE.md §3.1).
//
// mfsensorgroup.dll is resolved at runtime so this DLL still loads on Windows 10, where the API
// does not exist; AirLensVCam_Start then returns HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED).

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <wrl/client.h>

#pragma comment(lib, "mfplat.lib")

using Microsoft::WRL::ComPtr;

// {7B1F5C2A-3E44-4C1B-9A57-2D7A1E0C9B11}: CLSID of the AirLens media source COM class.
static constexpr wchar_t kSourceClsid[] = L"{7B1F5C2A-3E44-4C1B-9A57-2D7A1E0C9B11}";

namespace {

using CreateVirtualCameraFn = HRESULT(WINAPI*)(MFVirtualCameraType, MFVirtualCameraLifetime, MFVirtualCameraAccess,
                                               LPCWSTR, LPCWSTR, const GUID*, ULONG, IMFVirtualCamera**);
using IsTypeSupportedFn = HRESULT(WINAPI*)(MFVirtualCameraType, BOOL*);

SRWLOCK g_lock = SRWLOCK_INIT;
ComPtr<IMFVirtualCamera> g_camera;
bool g_mfStarted = false;

HMODULE SensorGroup() {
    static HMODULE module = LoadLibraryExW(L"mfsensorgroup.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    return module;
}

}  // namespace

// Creates (or re-opens: same parameters = same camera) and starts the virtual camera.
// Returns S_OK, S_FALSE if already running, or a failure HRESULT. E_ACCESSDENIED usually means the
// camera privacy setting blocks desktop apps.
extern "C" __declspec(dllexport) HRESULT __stdcall AirLensVCam_Start(LPCWSTR friendlyName) {
    AcquireSRWLockExclusive(&g_lock);
    HRESULT hr = S_OK;
    do {
        if (g_camera) { hr = S_FALSE; break; }

        HMODULE module = SensorGroup();
        auto isSupported = module ? reinterpret_cast<IsTypeSupportedFn>(GetProcAddress(module, "MFIsVirtualCameraTypeSupported")) : nullptr;
        auto create = module ? reinterpret_cast<CreateVirtualCameraFn>(GetProcAddress(module, "MFCreateVirtualCamera")) : nullptr;
        if (!isSupported || !create) { hr = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED); break; }  // pre-Windows 11

        BOOL supported = FALSE;
        hr = isSupported(MFVirtualCameraType_SoftwareCameraSource, &supported);
        if (FAILED(hr)) break;
        if (!supported) { hr = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED); break; }

        if (!g_mfStarted) {
            hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
            if (FAILED(hr)) break;
            g_mfStarted = true;
        }

        ComPtr<IMFVirtualCamera> camera;
        hr = create(MFVirtualCameraType_SoftwareCameraSource,
                    MFVirtualCameraLifetime_Session,     // gone when we Shutdown or the process exits
                    MFVirtualCameraAccess_CurrentUser,   // no admin rights required
                    friendlyName && *friendlyName ? friendlyName : L"AirLens",  // Windows appends "Windows Virtual Camera"
                    kSourceClsid,
                    nullptr, 0,                          // default categories: VIDEO_CAMERA, VIDEO, CAPTURE
                    &camera);
        if (FAILED(hr)) break;

        hr = camera->Start(nullptr);
        if (FAILED(hr)) {
            camera->Shutdown();
            break;
        }
        g_camera = camera;
    } while (false);
    ReleaseSRWLockExclusive(&g_lock);
    return hr;
}

// Stops and removes the session camera. Safe to call when not started.
extern "C" __declspec(dllexport) HRESULT __stdcall AirLensVCam_Stop() {
    AcquireSRWLockExclusive(&g_lock);
    HRESULT hr = S_FALSE;
    if (g_camera) {
        g_camera->Stop();
        hr = g_camera->Shutdown();
        g_camera.Reset();
    }
    if (g_mfStarted) {
        MFShutdown();
        g_mfStarted = false;
    }
    ReleaseSRWLockExclusive(&g_lock);
    return hr;
}

extern "C" __declspec(dllexport) BOOL __stdcall AirLensVCam_IsRunning() {
    AcquireSRWLockShared(&g_lock);
    BOOL running = g_camera ? TRUE : FALSE;
    ReleaseSRWLockShared(&g_lock);
    return running;
}
