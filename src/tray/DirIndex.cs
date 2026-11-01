using System.IO.MemoryMappedFiles;

namespace FavExplorer;

/// <summary>
/// The write side of the folder-hint index that HeartOverlay.dll reads from inside
/// explorer.exe.
///
/// Every byte of this must match src/overlay/index.cpp. If the two hash functions
/// ever disagree, the symptom is not a crash — it is hearts that quietly stop
/// appearing on someone's photos. See docs/index-format.md.
/// </summary>
internal sealed class DirIndex : IDisposable
{
    private const uint Magic     = 0x58444D48; // 'HMDX'
    private const uint Version   = 1;
    private const int  Capacity  = 65536;      // must stay a power of two
    private const int  HeaderSize = 64;
    private const long TotalBytes = HeaderSize + (long)Capacity * sizeof(ulong);

    private const ulong FnvBasis = 0xCBF29CE484222325UL;
    private const ulong FnvPrime = 0x100000001B3UL;

    // Refuse to fill the table beyond this. Realistically we hold a few hundred
    // folders against 65,536 slots, so this is a tripwire, not a limit anyone hits.
    private const int MaxEntries = Capacity / 2;

    private readonly string _path;
    private readonly object _gate = new();

    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _view;

    /// <summary>False once a write has failed and could not be recovered.</summary>
    public bool Healthy { get; private set; } = true;

    public DirIndex(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        (_mmf, _view) = Open(path);
        InitHeaderIfNeeded();
    }

    private static (MemoryMappedFile, MemoryMappedViewAccessor) Open(string path)
    {
        // The DLL maps this file for the lifetime of every Explorer process, so it
        // can never be replaced or resized — only written through in place. Fixed
        // capacity is what buys that simplicity.
        var mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.OpenOrCreate, null, TotalBytes, MemoryMappedFileAccess.ReadWrite);
        var view = mmf.CreateViewAccessor(0, TotalBytes, MemoryMappedFileAccess.ReadWrite);
        return (mmf, view);
    }

    private void InitHeaderIfNeeded()
    {
        if (_view.ReadUInt32(0) == Magic && _view.ReadUInt32(4) == Version) return;

        _view.Write(0, Magic);
        _view.Write(4, Version);
        _view.Write(8, 0UL);            // generation
        _view.Write(16, (uint)Capacity);
        _view.Write(20, 0u);            // count
        ClearSlots();
        _view.Flush();
    }

    /// <summary>
    /// FNV-1a over the UTF-16 units of an ASCII-lowercased path.
    ///
    /// The fold is ASCII-only on purpose: ToLowerInvariant and the C++ side's
    /// towlower disagree about a handful of non-ASCII characters, and a hash that
    /// disagrees across the two halves of the app is a bug you only discover on
    /// someone else's photo folder.
    /// </summary>
    public static ulong HashFolder(string path)
    {
        ulong h = FnvBasis;
        foreach (char raw in path)
        {
            char c = (raw >= 'A' && raw <= 'Z') ? (char)(raw + 32) : raw;
            h ^= (ulong)(c & 0xFF);        h *= FnvPrime;
            h ^= (ulong)((c >> 8) & 0xFF); h *= FnvPrime;
        }
        return h == 0 ? 1 : h;             // 0 is the empty-slot sentinel
    }

    /// <summary>
    /// Normalises a folder path the same way the DLL does when it slices the parent
    /// off a file path: no trailing separator, except on a drive root.
    /// </summary>
    public static string NormalizeFolder(string folder)
    {
        string f = folder.TrimEnd('\\', '/');
        if (f.Length == 2 && f[1] == ':') f += '\\';   // "C:" -> "C:\"
        return f;
    }

    public static ulong HashNormalizedFolder(string folder) => HashFolder(NormalizeFolder(folder));

    // ------------------------------------------------------------------ writing --

    /// <summary>
    /// Replaces the whole table with the folders given, then proves it worked.
    ///
    /// The proving is not paranoia. This index has already gone stale once in the
    /// field: the app kept saving its favourites list while the index silently held
    /// a folder set from hours earlier, and because a missing folder is answered with
    /// a legitimate "no favourites here", nothing anywhere reported a problem. A
    /// write that cannot be read back is now treated as a failure and repaired.
    /// </summary>
    public bool Replace(IEnumerable<string> folders)
    {
        var hashes = new HashSet<ulong>();
        foreach (string f in folders)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            hashes.Add(HashNormalizedFolder(f));
            if (hashes.Count > MaxEntries) break;
        }

        lock (_gate)
        {
            if (TryWrite(hashes)) { Healthy = true; return true; }

            // The mapping is the only thing between us and the file, so if the bytes
            // did not land, throw it away and build a fresh one.
            Log.Write($"index write did not take ({hashes.Count} folders expected) - remapping");
            if (Remap() && TryWrite(hashes))
            {
                Log.Write("index recovered after remap");
                Healthy = true;
                return true;
            }

            Log.Write("index STILL not writable after remap - hearts will not draw for new folders");
            Healthy = false;
            return false;
        }
    }

    private bool TryWrite(HashSet<ulong> hashes)
    {
        try
        {
            ulong gen = _view.ReadUInt64(8);

            // Seqlock: odd means "mid-write, don't trust the slots". A reader that
            // sees odd falls back to probing the stream, which is always correct.
            ulong writing = gen | 1;
            _view.Write(8, writing);
            Thread.MemoryBarrier();

            ClearSlots();
            foreach (ulong h in hashes) Insert(h);

            _view.Write(20, (uint)hashes.Count);
            Thread.MemoryBarrier();
            ulong final = writing + 1;      // back to even, one generation newer
            _view.Write(8, final);
            _view.Flush();

            return ReadBackMatches(final, hashes.Count);
        }
        catch (Exception ex)
        {
            Log.Write($"index write threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Confirms the write through a completely independent read path — a normal file
    /// handle, not our own mapping.
    ///
    /// Reading back through the same view we just wrote would prove nothing: the
    /// failure mode being guarded against is exactly the one where our view and the
    /// file have parted company, and the DLL in Explorer reads the file, not our view.
    /// </summary>
    private bool ReadBackMatches(ulong expectedGeneration, int expectedCount)
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[HeaderSize];
            if (fs.Read(header, 0, HeaderSize) != HeaderSize) return false;

            ulong gen = BitConverter.ToUInt64(header, 8);
            uint count = BitConverter.ToUInt32(header, 20);
            return gen == expectedGeneration && count == (uint)expectedCount;
        }
        catch (Exception ex)
        {
            Log.Write($"index read-back threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool Remap()
    {
        try
        {
            try { _view.Dispose(); } catch { }
            try { _mmf.Dispose(); } catch { }
            (_mmf, _view) = Open(_path);
            InitHeaderIfNeeded();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"index remap failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void ClearSlots()
    {
        // MemoryMappedViewAccessor has no bulk clear, and WriteArray of 65,536 longs
        // allocates. A zeroed scratch buffer written in chunks keeps it allocation-free
        // enough for something that runs on every toggle.
        var zeros = new ulong[1024];
        for (int i = 0; i < Capacity; i += zeros.Length)
            _view.WriteArray(HeaderSize + (long)i * sizeof(ulong), zeros, 0, Math.Min(zeros.Length, Capacity - i));
    }

    private void Insert(ulong hash)
    {
        const int mask = Capacity - 1;
        int i = (int)(hash & mask);
        for (int probes = 0; probes < Capacity; probes++)
        {
            ulong v = _view.ReadUInt64(HeaderSize + (long)i * sizeof(ulong));
            if (v == 0 || v == hash)
            {
                _view.Write(HeaderSize + (long)i * sizeof(ulong), hash);
                return;
            }
            i = (i + 1) & mask;
        }
    }

    // ------------------------------------------------------------------ reading --

    /// <summary>Reads the header the way the DLL does — off disk, not from our view.</summary>
    public (ulong Generation, int Count)? ReadHeaderFromDisk()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[HeaderSize];
            if (fs.Read(header, 0, HeaderSize) != HeaderSize) return null;
            return (BitConverter.ToUInt64(header, 8), (int)BitConverter.ToUInt32(header, 20));
        }
        catch
        {
            return null;
        }
    }

    public ulong Generation
    {
        get { lock (_gate) return _view.ReadUInt64(8); }
    }

    public int Count
    {
        get { lock (_gate) return (int)_view.ReadUInt32(20); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _view.Dispose(); } catch { }
            try { _mmf.Dispose(); } catch { }
        }
    }
}
