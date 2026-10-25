#include "Activator.h"
#include "Common.h"

namespace {

HMODULE g_module = nullptr;

class ClassFactory final : public IClassFactory {
    IMPLEMENT_REFCOUNT

public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        RETURN_HR_IF(E_POINTER, !ppv);
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        RETURN_HR_IF(E_POINTER, !ppv);
        *ppv = nullptr;
        RETURN_HR_IF(CLASS_E_NOAGGREGATION, outer);
        ComPtr<Activator> activator;
        activator.Attach(new (std::nothrow) Activator());
        RETURN_HR_IF(E_OUTOFMEMORY, !activator);
        RETURN_IF_FAILED(activator->Initialize());
        return activator->QueryInterface(riid, ppv);
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        lock ? ++g_objectCount : --g_objectCount;
        return S_OK;
    }
};

std::wstring ClsidString()
{
    wchar_t buffer[64];
    StringFromGUID2(CLSID_PhoneWebcamSource, buffer, _countof(buffer));
    return buffer;
}

std::wstring ModulePath()
{
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(g_module, path, MAX_PATH);
    return path;
}

// App-side virtual camera registration (see PhoneWebcam_StartVirtualCamera).
std::mutex g_vcamLock;
ComPtr<IMFVirtualCamera> g_vcam;
bool g_mfStarted = false;

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* ppv)
{
    RETURN_HR_IF(E_POINTER, !ppv);
    *ppv = nullptr;
    RETURN_HR_IF(CLASS_E_CLASSNOTAVAILABLE, clsid != CLSID_PhoneWebcamSource);
    ComPtr<ClassFactory> factory;
    factory.Attach(new (std::nothrow) ClassFactory());
    RETURN_HR_IF(E_OUTOFMEMORY, !factory);
    return factory->QueryInterface(riid, ppv);
}

/// Must be HKLM (admin): the Frame Server and Frame Server Monitor services load the DLL, not the user.
STDAPI DllRegisterServer()
{
    const std::wstring keyPath = L"Software\\Classes\\CLSID\\" + ClsidString() + L"\\InprocServer32";
    const std::wstring path = ModulePath();
    HKEY key = nullptr;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    status = RegSetValueExW(key, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(path.c_str()),
                            static_cast<DWORD>((path.size() + 1) * sizeof(wchar_t)));
    if (status == ERROR_SUCCESS) {
        const wchar_t model[] = L"Both";
        status = RegSetValueExW(key, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(model), sizeof(model));
    }
    RegCloseKey(key);
    return HRESULT_FROM_WIN32(status);
}

STDAPI DllUnregisterServer()
{
    const std::wstring keyPath = L"Software\\Classes\\CLSID\\" + ClsidString();
    LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, keyPath.c_str());
    return status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}

/// Called by the companion app (P/Invoke). Creates a session-lifetime, current-user virtual camera
/// (no admin needed at runtime). The camera disappears when Stop is called or the process exits.
/// Windows appends " (Windows Virtual Camera)" to the name.
extern "C" HRESULT __stdcall PhoneWebcam_StartVirtualCamera(LPCWSTR friendlyName)
{
    std::lock_guard lock(g_vcamLock);
    if (g_vcam) return S_FALSE;
    if (!g_mfStarted) {
        RETURN_IF_FAILED(MFStartup(MF_VERSION));
        g_mfStarted = true;
    }
    ComPtr<IMFVirtualCamera> vcam;
    RETURN_IF_FAILED(MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
                                           MFVirtualCameraAccess_CurrentUser, friendlyName ? friendlyName : L"Phone Webcam",
                                           ClsidString().c_str(), nullptr, 0, &vcam));
    RETURN_IF_FAILED(vcam->Start(nullptr));
    g_vcam = vcam;
    return S_OK;
}

extern "C" HRESULT __stdcall PhoneWebcam_StopVirtualCamera()
{
    std::lock_guard lock(g_vcamLock);
    if (!g_vcam) return S_FALSE;
    // Remove (not Shutdown): Shutdown would shut the media source down twice and block removal.
    HRESULT hr = g_vcam->Remove();
    g_vcam.Reset();
    return hr;
}
