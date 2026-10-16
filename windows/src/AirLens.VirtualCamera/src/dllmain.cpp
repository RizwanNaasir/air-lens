// In-proc COM server entry points for the AirLens virtual camera media source, plus tracing.
#include "Activator.h"

#include <cstdarg>
#include <cstdio>
#include <shlobj.h>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")

HMODULE g_module = nullptr;
std::atomic<long> g_objectCount{0};

void AirLensTrace(const char* format, ...) {
    char message[1024];
    va_list args;
    va_start(args, format);
    _vsnprintf_s(message, _TRUNCATE, format, args);
    va_end(args);

    SYSTEMTIME now;
    GetLocalTime(&now);
    char line[1200];
    _snprintf_s(line, _TRUNCATE, "%02u:%02u:%02u.%03u [%lu] %s\r\n", now.wHour, now.wMinute, now.wSecond,
                now.wMilliseconds, GetCurrentProcessId(), message);
    OutputDebugStringA(line);

    // %ProgramData%\AirLens\vcam.log; Frame Server (Local Service) can write there. Best effort.
    static std::mutex fileLock;
    std::lock_guard<std::mutex> guard(fileLock);
    wchar_t* programData = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &programData))) return;
    wchar_t dir[MAX_PATH], path[MAX_PATH];
    swprintf_s(dir, L"%s\\AirLens", programData);
    swprintf_s(path, L"%s\\vcam.log", dir);
    CoTaskMemFree(programData);
    CreateDirectoryW(dir, nullptr);
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER size{};
    if (GetFileSizeEx(file, &size) && size.QuadPart < 1'000'000) {
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(strlen(line)), &written, nullptr);
    }
    CloseHandle(file);
}

namespace {

class ClassFactory final : public IClassFactory {
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }   // static instance
    STDMETHODIMP_(ULONG) Release() override { return 1; }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        ComPtr<Activator> activator;
        activator.Attach(new (std::nothrow) Activator());
        if (!activator) return E_OUTOFMEMORY;
        AIRLENS_RETURN_IF_FAILED(activator->Initialize());
        return activator->QueryInterface(riid, ppv);
    }

    STDMETHODIMP LockServer(BOOL lock) override {
        if (lock) ++g_objectCount;
        else --g_objectCount;
        return S_OK;
    }
};

ClassFactory g_factory;

constexpr wchar_t kClsidKey[] = L"Software\\Classes\\CLSID\\{7B1F5C2A-3E44-4C1B-9A57-2D7A1E0C9B11}";

LSTATUS SetString(HKEY key, const wchar_t* name, const wchar_t* value) {
    return RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value),
                          static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_AirLensVirtualCamera) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() { return g_objectCount == 0 ? S_OK : S_FALSE; }

// HKLM, because Frame Server (Local Service) and Frame Server Monitor (Local System) load the
// source and cannot see a user's HKCU classes. Needs elevation, once, at install time.
STDAPI DllRegisterServer() {
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());
    HKEY clsidKey = nullptr, serverKey = nullptr;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kClsidKey, 0, nullptr, 0, KEY_WRITE, nullptr, &clsidKey, nullptr);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    SetString(clsidKey, nullptr, L"AirLens Virtual Camera Source");
    status = RegCreateKeyExW(clsidKey, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &serverKey, nullptr);
    if (status == ERROR_SUCCESS) {
        status = SetString(serverKey, nullptr, path);
        if (status == ERROR_SUCCESS) status = SetString(serverKey, L"ThreadingModel", L"Both");
        RegCloseKey(serverKey);
    }
    RegCloseKey(clsidKey);
    return HRESULT_FROM_WIN32(status);
}

STDAPI DllUnregisterServer() {
    const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey);
    return (status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND) ? S_OK : HRESULT_FROM_WIN32(status);
}
