// DLL entry points and self-registration.

#include "favexplorer.h"

HINSTANCE g_hInst = nullptr;
LONG      g_cRefModule = 0;

extern "C" const CLSID CLSID_HeartOverlay =
    { 0x9004651E, 0xE74B, 0x4F8C, { 0xA7, 0x9E, 0x7D, 0x9F, 0xA5, 0x6D, 0x62, 0xEA } };

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        g_hInst = (HINSTANCE)hModule;
        // Explorer spins up a lot of threads. We have no per-thread state, and
        // doing nothing in the loader lock is the only safe thing anyway.
        DisableThreadLibraryCalls(hModule);
        hintindex::Init();
        break;
    case DLL_PROCESS_DETACH:
        hintindex::Shutdown();
        break;
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (rclsid != CLSID_HeartOverlay) return CLASS_E_CLASSNOTAVAILABLE;

    ClassFactory* f = new (std::nothrow) ClassFactory();
    if (!f) return E_OUTOFMEMORY;

    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    return (g_cRefModule == 0) ? S_OK : S_FALSE;
}

// ------------------------------------------------------------- registration --

namespace {

LONG SetValue(HKEY root, const wchar_t* subkey, const wchar_t* name, const wchar_t* value) {
    HKEY hKey = nullptr;
    LONG rc = RegCreateKeyExW(root, subkey, 0, nullptr, REG_OPTION_NON_VOLATILE,
                              KEY_SET_VALUE | KEY_WOW64_64KEY, nullptr, &hKey, nullptr);
    if (rc != ERROR_SUCCESS) return rc;

    size_t len = 0;
    StringCchLengthW(value, STRSAFE_MAX_CCH, &len);
    rc = RegSetValueExW(hKey, name, 0, REG_SZ, (const BYTE*)value,
                        (DWORD)((len + 1) * sizeof(wchar_t)));
    RegCloseKey(hKey);
    return rc;
}

const wchar_t* kClsidKey =
    L"SOFTWARE\\Classes\\CLSID\\" FAVEXPLORER_CLSID_STR;
const wchar_t* kInprocKey =
    L"SOFTWARE\\Classes\\CLSID\\" FAVEXPLORER_CLSID_STR L"\\InprocServer32";
const wchar_t* kOverlayRoot =
    L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers";
const wchar_t* kOverlayKey =
    L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers\\"
    FAVEXPLORER_OVERLAY_KEYNAME;

} // namespace

STDAPI DllRegisterServer() {
    wchar_t module[MAX_PATH];
    if (!GetModuleFileNameW(g_hInst, module, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());

    LONG rc;
    if ((rc = SetValue(HKEY_LOCAL_MACHINE, kClsidKey,  nullptr, FAVEXPLORER_FRIENDLY)) != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(rc);
    if ((rc = SetValue(HKEY_LOCAL_MACHINE, kInprocKey, nullptr, module)) != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(rc);
    if ((rc = SetValue(HKEY_LOCAL_MACHINE, kInprocKey, L"ThreadingModel", L"Apartment")) != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(rc);

    // This is the key that actually makes Explorer ask us anything.
    if ((rc = SetValue(HKEY_LOCAL_MACHINE, kOverlayKey, nullptr, FAVEXPLORER_CLSID_STR)) != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(rc);

    return S_OK;
}

STDAPI DllUnregisterServer() {
    HKEY hRoot = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, kOverlayRoot, 0,
                      KEY_SET_VALUE | KEY_WOW64_64KEY, &hRoot) == ERROR_SUCCESS) {
        RegDeleteKeyExW(hRoot, FAVEXPLORER_OVERLAY_KEYNAME, KEY_WOW64_64KEY, 0);
        RegCloseKey(hRoot);
    }

    HKEY hClasses = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Classes\\CLSID", 0,
                      KEY_SET_VALUE | KEY_WOW64_64KEY, &hClasses) == ERROR_SUCCESS) {
        RegDeleteKeyExW(hClasses, FAVEXPLORER_CLSID_STR L"\\InprocServer32", KEY_WOW64_64KEY, 0);
        RegDeleteKeyExW(hClasses, FAVEXPLORER_CLSID_STR, KEY_WOW64_64KEY, 0);
        RegCloseKey(hClasses);
    }
    return S_OK;
}
