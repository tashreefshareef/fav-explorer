// Fav Explorer — shared declarations for the shell icon overlay handler.
//
// This DLL is loaded into explorer.exe. Everything in here runs on threads that
// draw the user's desktop, so the rules are: no blocking I/O on the hot path, no
// allocations we can avoid, and no way to fail that isn't "quietly show no heart".

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shlobj.h>
#include <strsafe.h>
#include <stdint.h>
#include <new>

// {9004651E-E74B-4F8C-A79E-7D9FA56D62EA}
// Not Heartmark's CLSID: uninstalling Heartmark deletes its CLSID key, which would
// take ours with it if the two were shared.
extern "C" const CLSID CLSID_HeartOverlay;

#define FAVEXPLORER_CLSID_STR   L"{9004651E-E74B-4F8C-A79E-7D9FA56D62EA}"
#define FAVEXPLORER_FRIENDLY    L"Fav Explorer Favourite Overlay"

// Three leading spaces. Windows sorts the overlay identifiers ordinally and only
// honours the first ~11 survivors, so the name is load-bearing: it is how we win a
// slot against OneDrive and friends. See docs/overlay-slots.md.
#define FAVEXPLORER_OVERLAY_KEYNAME  L"   FavExplorer"

#define FAVEXPLORER_STREAM      L":favexplorer"

extern HINSTANCE g_hInst;
extern LONG      g_cRefModule;

// ---------------------------------------------------------------- hint index --

// Answers "could anything in this folder be a favourite?" from a memory-mapped
// table the tray app maintains. Format is specified in docs/index-format.md and
// must stay byte-identical to the C# writer in src/tray/DirIndex.cs.
namespace hintindex {

// FNV-1a over the UTF-16 units of an ASCII-lowercased path. Must match
// DirIndex.HashFolder in the tray app exactly.
uint64_t HashFolder(const wchar_t* path, size_t len);

// Extracts the parent folder of `path` and hashes it. Returns false if the path
// has no parent we can make sense of.
bool HashParentFolder(const wchar_t* path, size_t len, uint64_t* out);

void Init();
void Shutdown();

// true  -> this folder might hold favourites, go probe the stream
// false -> definitely nothing here; the caller must not touch the disk
bool FolderMayHaveFavourites(uint64_t folderHash);

// Bumped whenever the tray app rewrites the table. Used to expire cached results.
uint64_t Generation();

} // namespace hintindex

// ------------------------------------------------------------------ the probe --

// Opens <path>:favexplorer to find out whether the file is actually tagged. Only
// ever called for paths that survived the hint-index rejection.
bool HasHeartStream(const wchar_t* path, size_t len);

// ------------------------------------------------------------------ COM object --

class ClassFactory : public IClassFactory {
public:
    ClassFactory();
    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv);
    IFACEMETHODIMP_(ULONG) AddRef();
    IFACEMETHODIMP_(ULONG) Release();
    // IClassFactory
    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv);
    IFACEMETHODIMP LockServer(BOOL fLock);
private:
    ~ClassFactory();
    LONG m_cRef;
};

class HeartOverlay : public IShellIconOverlayIdentifier {
public:
    HeartOverlay();
    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv);
    IFACEMETHODIMP_(ULONG) AddRef();
    IFACEMETHODIMP_(ULONG) Release();
    // IShellIconOverlayIdentifier
    IFACEMETHODIMP GetOverlayInfo(LPWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags);
    IFACEMETHODIMP GetPriority(int* pIPriority);
    IFACEMETHODIMP IsMemberOf(LPCWSTR pwszPath, DWORD dwAttrib);
private:
    ~HeartOverlay();
    LONG m_cRef;
};
