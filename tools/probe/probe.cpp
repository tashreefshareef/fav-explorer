// probe.exe — asks the overlay handler's own code the same question Explorer asks,
// but from a console where you can see the answer.
//
// This exists because the failure mode it guards against is invisible. If the C++
// hash in index.cpp and the C# hash in DirIndex.cs ever drift apart, nothing
// crashes and no error appears — hearts just quietly stop being drawn. Compiling
// the real index.cpp into a test harness is the only way to check the two halves
// still agree.
//
//   probe.exe <path> [<path>...]   verdict for each file
//   probe.exe --hash <folder>      the raw folder hash, to compare against C#

#include "../../src/overlay/favexplorer.h"
#include <cstdio>
#include <string>

// The COM object lives in overlay.cpp, which we deliberately don't link here.
// These satisfy index.cpp's references without dragging Explorer's world in.
HINSTANCE g_hInst = nullptr;
LONG g_cRefModule = 0;

static void PrintVerdict(const wchar_t* path) {
    size_t len = wcslen(path);

    wprintf(L"\n%s\n", path);

    uint64_t folderHash = 0;
    if (!hintindex::HashParentFolder(path, len, &folderHash)) {
        wprintf(L"  no usable parent folder -> S_FALSE\n");
        return;
    }
    wprintf(L"  parent hash        0x%016llX\n", (unsigned long long)folderHash);

    bool hinted = hintindex::FolderMayHaveFavourites(folderHash);
    wprintf(L"  folder in index    %s\n", hinted ? L"yes - will probe the stream"
                                                 : L"no  - rejected with no disk access");
    if (!hinted) {
        wprintf(L"  VERDICT            S_FALSE (no heart)\n");
        return;
    }

    bool tagged = HasHeartStream(path, len);
    wprintf(L"  stream present     %s\n", tagged ? L"yes" : L"no");
    wprintf(L"  VERDICT            %s\n", tagged ? L"S_OK (heart drawn)" : L"S_FALSE (no heart)");
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) {
        wprintf(L"usage: probe <path> [<path>...]\n");
        wprintf(L"       probe --hash <folder>\n");
        return 2;
    }

    if (wcscmp(argv[1], L"--hash") == 0) {
        if (argc < 3) return 2;
        // Matches what the C# side calls HashNormalizedFolder.
        std::wstring f = argv[2];
        while (f.size() > 3 && (f.back() == L'\\' || f.back() == L'/')) f.pop_back();
        if (f.size() == 2 && f[1] == L':') f += L'\\';
        wprintf(L"%s -> 0x%016llX\n", f.c_str(),
                (unsigned long long)hintindex::HashFolder(f.c_str(), f.size()));
        return 0;
    }

    for (int i = 1; i < argc; ++i) PrintVerdict(argv[i]);

    // Printed last, not first: the index is mapped lazily on the first lookup, so
    // asking before then always reports zero and looks like a broken index.
    wprintf(L"\nindex generation   %llu\n", (unsigned long long)hintindex::Generation());
    return 0;
}
