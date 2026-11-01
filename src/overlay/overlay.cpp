// The COM object Explorer actually talks to.

#include "favexplorer.h"

// ------------------------------------------------------------- result cache --
//
// Explorer re-asks about the same items constantly — every repaint, every scroll,
// every view change. Without a cache, a folder full of favourites would re-open a
// stream per item per repaint. Direct-mapped and tiny; a collision just costs a
// re-probe, which is the same work we'd have done anyway.

namespace {

struct CacheEntry {
    uint64_t pathHash;
    uint64_t generation;
    bool     isFavourite;
    bool     valid;
};

const size_t kCacheSlots = 1024;
CacheEntry g_cache[kCacheSlots];
SRWLOCK    g_cacheLock = SRWLOCK_INIT;

bool CacheGet(uint64_t pathHash, uint64_t gen, bool* out) {
    bool hit = false;
    AcquireSRWLockShared(&g_cacheLock);
    const CacheEntry& e = g_cache[pathHash & (kCacheSlots - 1)];
    if (e.valid && e.pathHash == pathHash && e.generation == gen) {
        *out = e.isFavourite;
        hit = true;
    }
    ReleaseSRWLockShared(&g_cacheLock);
    return hit;
}

void CachePut(uint64_t pathHash, uint64_t gen, bool isFavourite) {
    AcquireSRWLockExclusive(&g_cacheLock);
    CacheEntry& e = g_cache[pathHash & (kCacheSlots - 1)];
    e.pathHash    = pathHash;
    e.generation  = gen;
    e.isFavourite = isFavourite;
    e.valid       = true;
    ReleaseSRWLockExclusive(&g_cacheLock);
}

} // namespace

// -------------------------------------------------------------- HeartOverlay --

HeartOverlay::HeartOverlay() : m_cRef(1) {
    InterlockedIncrement(&g_cRefModule);
}

HeartOverlay::~HeartOverlay() {
    InterlockedDecrement(&g_cRefModule);
}

IFACEMETHODIMP HeartOverlay::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IShellIconOverlayIdentifier) {
        *ppv = static_cast<IShellIconOverlayIdentifier*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

IFACEMETHODIMP_(ULONG) HeartOverlay::AddRef() {
    return InterlockedIncrement(&m_cRef);
}

IFACEMETHODIMP_(ULONG) HeartOverlay::Release() {
    LONG c = InterlockedDecrement(&m_cRef);
    if (c == 0) delete this;
    return c;
}

IFACEMETHODIMP HeartOverlay::GetOverlayInfo(LPWSTR pwszIconFile, int cchMax,
                                            int* pIndex, DWORD* pdwFlags) {
    if (!pwszIconFile || !pIndex || !pdwFlags || cchMax <= 0) return E_INVALIDARG;

    // The heart lives as the first icon resource in this very DLL, so Explorer can
    // load it without us shipping a loose .ico anyone could delete.
    DWORD n = GetModuleFileNameW(g_hInst, pwszIconFile, (DWORD)cchMax);
    if (n == 0 || n >= (DWORD)cchMax) return E_FAIL;

    *pIndex   = 0;
    *pdwFlags = ISIOI_ICONFILE | ISIOI_ICONINDEX;
    return S_OK;
}

IFACEMETHODIMP HeartOverlay::GetPriority(int* pIPriority) {
    if (!pIPriority) return E_INVALIDARG;
    // 0 is the highest priority. If another handler also claims a file, we'd rather
    // the user's own explicit favourite win over an automatic sync-status badge.
    *pIPriority = 0;
    return S_OK;
}

IFACEMETHODIMP HeartOverlay::IsMemberOf(LPCWSTR pwszPath, DWORD dwAttrib) {
    UNREFERENCED_PARAMETER(dwAttrib);
    if (!pwszPath) return S_FALSE;

    size_t len = 0;
    if (FAILED(StringCchLengthW(pwszPath, 32768, &len)) || len < 3) return S_FALSE;

    // Anything that isn't a real filesystem path — a virtual shell folder, a search
    // result, Control Panel — can't carry a stream. Bail before doing any work.
    if (pwszPath[1] != L':' && !(pwszPath[0] == L'\\' && pwszPath[1] == L'\\'))
        return S_FALSE;

    uint64_t folderHash = 0;
    if (!hintindex::HashParentFolder(pwszPath, len, &folderHash)) return S_FALSE;

    // The rejection that makes this whole design viable: no disk, no allocation,
    // one cache line. True for essentially every file the user ever looks at.
    if (!hintindex::FolderMayHaveFavourites(folderHash)) return S_FALSE;

    uint64_t gen = hintindex::Generation();
    uint64_t pathHash = hintindex::HashFolder(pwszPath, len);

    bool fav = false;
    if (!CacheGet(pathHash, gen, &fav)) {
        fav = HasHeartStream(pwszPath, len);
        CachePut(pathHash, gen, fav);
    }

    return fav ? S_OK : S_FALSE;
}

// -------------------------------------------------------------- ClassFactory --

ClassFactory::ClassFactory() : m_cRef(1) {
    InterlockedIncrement(&g_cRefModule);
}

ClassFactory::~ClassFactory() {
    InterlockedDecrement(&g_cRefModule);
}

IFACEMETHODIMP ClassFactory::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IClassFactory) {
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

IFACEMETHODIMP_(ULONG) ClassFactory::AddRef() {
    return InterlockedIncrement(&m_cRef);
}

IFACEMETHODIMP_(ULONG) ClassFactory::Release() {
    LONG c = InterlockedDecrement(&m_cRef);
    if (c == 0) delete this;
    return c;
}

IFACEMETHODIMP ClassFactory::CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (pUnkOuter) return CLASS_E_NOAGGREGATION;

    HeartOverlay* obj = new (std::nothrow) HeartOverlay();
    if (!obj) return E_OUTOFMEMORY;

    HRESULT hr = obj->QueryInterface(riid, ppv);
    obj->Release();
    return hr;
}

IFACEMETHODIMP ClassFactory::LockServer(BOOL fLock) {
    if (fLock) InterlockedIncrement(&g_cRefModule);
    else       InterlockedDecrement(&g_cRefModule);
    return S_OK;
}
