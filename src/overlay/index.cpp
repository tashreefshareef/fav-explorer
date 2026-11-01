// The read side of the folder-hint index.
//
// The whole point of this file is to answer "no" for almost every file on the
// machine without touching the disk. Explorer asks about every icon it draws; if
// that question ever costs a file open, scrolling a photo folder gets sticky.

#include "favexplorer.h"

namespace hintindex {

namespace {

const uint32_t kMagic    = 0x58444D48; // 'HMDX'
const uint32_t kVersion  = 1;
const uint64_t kFnvBasis = 0xCBF29CE484222325ULL;
const uint64_t kFnvPrime = 0x100000001B3ULL;

#pragma pack(push, 1)
struct Header {
    uint32_t magic;
    uint32_t version;
    uint64_t generation;
    uint32_t capacity;
    uint32_t count;
    uint8_t  reserved[40];
};
#pragma pack(pop)
static_assert(sizeof(Header) == 64, "header must be exactly 64 bytes");

HANDLE          g_hFile = INVALID_HANDLE_VALUE;
HANDLE          g_hMap  = nullptr;
const uint8_t*  g_view  = nullptr;
SRWLOCK         g_lock  = SRWLOCK_INIT;
ULONGLONG       g_lastTry = 0;
bool            g_tried = false;

// The tray app might not have started yet, or might be mid-install. Retrying the
// mapping on every single icon draw would be its own performance bug, so back off.
const ULONGLONG kRetryAfterMs = 3000;

bool MapLocked() {
    if (g_view) return true;

    ULONGLONG now = GetTickCount64();
    if (g_tried && (now - g_lastTry) < kRetryAfterMs) return false;
    g_lastTry = now;
    g_tried = true;

    wchar_t path[MAX_PATH];
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, path)))
        return false;
    if (FAILED(StringCchCatW(path, MAX_PATH, L"\\FavExplorer\\dirs.bin")))
        return false;

    HANDLE hFile = CreateFileW(path, GENERIC_READ,
                               FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(hFile, &size) || size.QuadPart < (LONGLONG)sizeof(Header)) {
        CloseHandle(hFile);
        return false;
    }

    HANDLE hMap = CreateFileMappingW(hFile, nullptr, PAGE_READONLY, 0, 0, nullptr);
    if (!hMap) { CloseHandle(hFile); return false; }

    const uint8_t* view = (const uint8_t*)MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0);
    if (!view) { CloseHandle(hMap); CloseHandle(hFile); return false; }

    const Header* h = (const Header*)view;
    // Reject anything we don't understand rather than guessing at it. A capacity
    // that isn't a power of two would break the probe mask.
    if (h->magic != kMagic || h->version != kVersion ||
        h->capacity == 0 || (h->capacity & (h->capacity - 1)) != 0 ||
        (uint64_t)size.QuadPart < sizeof(Header) + (uint64_t)h->capacity * 8) {
        UnmapViewOfFile(view);
        CloseHandle(hMap);
        CloseHandle(hFile);
        return false;
    }

    g_hFile = hFile;
    g_hMap  = hMap;
    g_view  = view;
    return true;
}

} // namespace

uint64_t HashFolder(const wchar_t* path, size_t len) {
    uint64_t h = kFnvBasis;
    for (size_t i = 0; i < len; ++i) {
        wchar_t c = path[i];
        // ASCII-only fold. Deliberately not towlower — see docs/index-format.md.
        if (c >= L'A' && c <= L'Z') c = (wchar_t)(c + 32);
        h ^= (uint64_t)(c & 0xFF);  h *= kFnvPrime;
        h ^= (uint64_t)((c >> 8) & 0xFF); h *= kFnvPrime;
    }
    return h ? h : 1; // 0 is the empty-slot sentinel
}

bool HashParentFolder(const wchar_t* path, size_t len, uint64_t* out) {
    if (!path || len < 3) return false;

    size_t cut = (size_t)-1;
    for (size_t i = len; i-- > 0; ) {
        if (path[i] == L'\\' || path[i] == L'/') { cut = i; break; }
    }
    if (cut == (size_t)-1) return false;

    // "C:\photo.jpg" -> parent is "C:\", keeping the separator. Everything else
    // drops it, so "C:\pics\a.jpg" -> "C:\pics".
    size_t parentLen = cut;
    if (parentLen == 2 && path[1] == L':') parentLen = 3;
    if (parentLen == 0) return false;

    *out = HashFolder(path, parentLen);
    return true;
}

void Init() {
    // Deliberately empty. Mapping happens lazily on the first question, because at
    // DllMain time we must not touch the filesystem or the loader lock.
}

void Shutdown() {
    AcquireSRWLockExclusive(&g_lock);
    if (g_view) { UnmapViewOfFile(g_view); g_view = nullptr; }
    if (g_hMap) { CloseHandle(g_hMap); g_hMap = nullptr; }
    if (g_hFile != INVALID_HANDLE_VALUE) { CloseHandle(g_hFile); g_hFile = INVALID_HANDLE_VALUE; }
    ReleaseSRWLockExclusive(&g_lock);
}

uint64_t Generation() {
    uint64_t gen = 0;
    AcquireSRWLockShared(&g_lock);
    if (g_view) gen = ((volatile const Header*)g_view)->generation;
    ReleaseSRWLockShared(&g_lock);
    return gen;
}

bool FolderMayHaveFavourites(uint64_t folderHash) {
    // Fast path first: shared lock, and if we're already mapped nobody blocks.
    AcquireSRWLockShared(&g_lock);
    bool mapped = (g_view != nullptr);
    ReleaseSRWLockShared(&g_lock);

    if (!mapped) {
        AcquireSRWLockExclusive(&g_lock);
        mapped = MapLocked();
        ReleaseSRWLockExclusive(&g_lock);
        // No index means the tray app has never recorded a favourite. "No" is the
        // correct answer, and it is the one that costs nothing.
        if (!mapped) return false;
    }

    bool result = false;
    AcquireSRWLockShared(&g_lock);
    if (g_view) {
        volatile const Header* h = (volatile const Header*)g_view;
        uint64_t gen1 = h->generation;

        if (gen1 & 1) {
            // Writer is mid-rewrite. Say "maybe" and let the stream probe decide;
            // one wasted file open beats a wrong answer.
            result = true;
        } else {
            const uint32_t cap  = h->capacity;
            const uint32_t mask = cap - 1;
            const uint64_t* slots = (const uint64_t*)(g_view + sizeof(Header));

            uint32_t i = (uint32_t)(folderHash & mask);
            for (uint32_t probes = 0; probes < cap; ++probes) {
                uint64_t v = slots[i];
                if (v == 0) break;              // empty slot ends the chain
                if (v == folderHash) { result = true; break; }
                i = (i + 1) & mask;
            }

            // Seqlock check: if the table shifted under us, fall back to "maybe".
            uint64_t gen2 = h->generation;
            if (gen2 != gen1) result = true;
        }
    }
    ReleaseSRWLockShared(&g_lock);
    return result;
}

} // namespace hintindex

// ------------------------------------------------------------------- the probe --

bool HasHeartStream(const wchar_t* path, size_t len) {
    // Long paths need the \\?\ prefix, and the stream suffix adds a few more units.
    // Stack buffer covers everything normal; the heap case is rare enough not to
    // matter for speed.
    const size_t kSuffix = _countof(FAVEXPLORER_STREAM) - 1; // ":favexplorer"
    const size_t kPrefix = 4;  // "\\?\"
    wchar_t stackBuf[MAX_PATH + kSuffix + kPrefix + 1];

    bool needPrefix = (len + kSuffix) >= MAX_PATH &&
                      !(len > 2 && path[0] == L'\\' && path[1] == L'\\');
    size_t need = len + kSuffix + (needPrefix ? kPrefix : 0) + 1;

    wchar_t* buf = stackBuf;
    wchar_t* heap = nullptr;
    if (need > _countof(stackBuf)) {
        heap = (wchar_t*)HeapAlloc(GetProcessHeap(), 0, need * sizeof(wchar_t));
        if (!heap) return false;
        buf = heap;
    }

    size_t o = 0;
    if (needPrefix) { buf[0] = L'\\'; buf[1] = L'\\'; buf[2] = L'?'; buf[3] = L'\\'; o = 4; }
    memcpy(buf + o, path, len * sizeof(wchar_t));
    o += len;
    memcpy(buf + o, FAVEXPLORER_STREAM, kSuffix * sizeof(wchar_t));
    o += kSuffix;
    buf[o] = 0;

    // Access 0 / FILE_READ_ATTRIBUTES is the cheapest way to ask "does this stream
    // exist". BACKUP_SEMANTICS lets it work for tagged folders too.
    HANDLE h = CreateFileW(buf, FILE_READ_ATTRIBUTES,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);

    bool found = (h != INVALID_HANDLE_VALUE);
    if (found) CloseHandle(h);
    if (heap) HeapFree(GetProcessHeap(), 0, heap);
    return found;
}
