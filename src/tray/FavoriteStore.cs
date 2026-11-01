using System.Globalization;
using System.Runtime.InteropServices;

namespace FavExplorer;

internal sealed record FavoriteEntry(string Path, DateTimeOffset AddedUtc);

internal sealed record ToggleResult(int Added, int Removed, int Skipped, string? SkipReason)
{
    public int Touched => Added + Removed;
    public static readonly ToggleResult Nothing = new(0, 0, 0, null);
}

internal sealed record IndexStatus(
    bool Healthy, int FoldersOnDisk, int FoldersExpected, int Favourites, ulong Generation, int Watching)
{
    public bool InSync => Healthy && FoldersOnDisk == FoldersExpected;
}

/// <summary>
/// Ties the three representations together: the stream on each file (the truth), the
/// folder-hint index the overlay DLL reads, and a flat list so the favourites window
/// has something to show without walking the disk.
/// </summary>

internal sealed class FavoriteStore : IDisposable
{
    private readonly DirIndex _index;
    private readonly FolderWatcher _watcher = new();
    private readonly Dictionary<string, DateTimeOffset> _favs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public event Action? Changed;

    public FavoriteStore()
    {
        Paths.Ensure();
        _index = new DirIndex(Paths.Index);
        LoadList();
        PublishIndex();
        _watcher.Changed += OnFolderChanged;
    }

    // ------------------------------------------------------------- watching --

    /// <summary>
    /// Applies what the watcher saw: a rename moves the entry, an arriving file that
    /// already carries a stream joins the list, and a vanished one leaves it.
    ///
    /// The tag on the file is always the truth; this only keeps our record of where
    /// that file lives from going stale underneath it.
    /// </summary>
    private void OnFolderChanged(IReadOnlyList<PathRename> renames, IReadOnlyList<string> touched)
    {
        bool dirty = false;

        foreach (PathRename r in renames)
        {
            lock (_gate)
            {
                if (_favs.TryGetValue(r.OldPath, out DateTimeOffset when))
                {
                    _favs.Remove(r.OldPath);
                    _favs[r.NewPath] = when;
                    dirty = true;
                }
            }
            // A rename can also carry a tagged file *into* view for the first time.
            if (!dirty && AdsTag.IsTagged(r.NewPath))
            {
                lock (_gate)
                {
                    if (!_favs.ContainsKey(r.NewPath))
                    {
                        _favs[r.NewPath] = AdsTag.ReadTaggedAt(r.NewPath) ?? DateTimeOffset.UtcNow;
                        dirty = true;
                    }
                }
            }
        }

        foreach (string p in touched)
        {
            bool known;
            lock (_gate) known = _favs.ContainsKey(p);

            if (known)
            {
                // Gone, or moved out and stripped of its stream.
                if (!File.Exists(p) && !Directory.Exists(p))
                {
                    lock (_gate) _favs.Remove(p);
                    dirty = true;
                }
            }
            else if ((File.Exists(p) || Directory.Exists(p)) && AdsTag.IsTagged(p))
            {
                // Moved in from elsewhere, stream intact. This is the case that used
                // to need a manual rescan.
                lock (_gate) _favs[p] = AdsTag.ReadTaggedAt(p) ?? DateTimeOffset.UtcNow;
                dirty = true;
            }
        }

        if (!dirty) return;

        Log.Write($"watcher applied {renames.Count} rename(s), {touched.Count} change(s)");
        PublishIndex();
        SaveList();
        Changed?.Invoke();
    }

    public int Count { get { lock (_gate) return _favs.Count; } }

    public List<FavoriteEntry> Snapshot()
    {
        lock (_gate)
            return _favs.Select(kv => new FavoriteEntry(kv.Key, kv.Value))
                        .OrderByDescending(e => e.AddedUtc)
                        .ToList();
    }

    public bool IsFavorite(string path)
    {
        lock (_gate) return _favs.ContainsKey(path);
    }

    // ------------------------------------------------------------- the toggle --

    /// <summary>
    /// Mirrors how a phone gallery behaves with a multi-selection: if anything in the
    /// selection is not yet a favourite, favourite the lot. Only when every single
    /// one is already hearted does the shortcut take them all off.
    /// </summary>
    public ToggleResult Toggle(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return ToggleResult.Nothing;

        var usable = new List<string>(paths.Count);
        int skipped = 0;
        string? skipReason = null;

        foreach (string p in paths)
        {
            if (!File.Exists(p) && !Directory.Exists(p)) { skipped++; continue; }
            if (!AdsTag.VolumeSupportsStreams(p))
            {
                skipped++;
                skipReason = "that drive isn't NTFS, so it can't hold the tag";
                continue;
            }
            usable.Add(p);
        }

        if (usable.Count == 0) return new ToggleResult(0, 0, skipped, skipReason);

        bool anyUnhearted;
        lock (_gate) anyUnhearted = usable.Any(p => !_favs.ContainsKey(p));

        int added = 0, removed = 0;
        var touched = new List<string>(usable.Count);

        foreach (string p in usable)
        {
            if (anyUnhearted)
            {
                if (!AdsTag.Tag(p)) { skipped++; skipReason ??= "the file is read-only or in use"; continue; }
                lock (_gate) _favs[p] = DateTimeOffset.UtcNow;
                added++;
            }
            else
            {
                if (!AdsTag.Untag(p)) { skipped++; skipReason ??= "the file is read-only or in use"; continue; }
                lock (_gate) _favs.Remove(p);
                removed++;
            }
            touched.Add(p);
        }

        if (touched.Count > 0)
        {
            PublishIndex();
            SaveList();
            RefreshIcons(touched);
            Changed?.Invoke();
        }

        return new ToggleResult(added, removed, skipped, skipReason);
    }

    public void Remove(string path)
    {
        AdsTag.Untag(path);
        lock (_gate) _favs.Remove(path);
        PublishIndex();
        SaveList();
        RefreshIcons(new[] { path });
        Changed?.Invoke();
    }

    // -------------------------------------------------------------- rebuilding --

    /// <summary>
    /// Drops entries whose file is gone or whose stream has been stripped — which is
    /// what a round trip through a ZIP or a FAT32 stick does to the tag.
    /// </summary>
    public int Reconcile()
    {
        List<string> known;
        lock (_gate) known = _favs.Keys.ToList();

        var dead = new List<string>();
        foreach (string p in known)
        {
            bool exists = File.Exists(p) || Directory.Exists(p);
            if (!exists || !AdsTag.IsTagged(p)) dead.Add(p);
        }

        if (dead.Count > 0)
        {
            lock (_gate) foreach (string p in dead) _favs.Remove(p);
            PublishIndex();
            SaveList();
            Changed?.Invoke();
        }
        return dead.Count;
    }

    /// <summary>
    /// Walks a folder looking for streams we don't know about. This is how a photo
    /// that was moved between folders — which keeps its stream but loses its place in
    /// our list — gets picked back up.
    /// </summary>
    public int Rescan(string folder, bool recursive, CancellationToken ct = default)
    {
        int found = 0;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (string p in Directory.EnumerateFiles(folder, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) if (_favs.ContainsKey(p)) continue;
                // A file hearted under the old name and moved since is invisible to
                // LegacyMigration, which only knows the old list. This catches it.
                if (!AdsTag.IsTagged(p) && !AdsTag.MigrateLegacy(p)) continue;

                DateTimeOffset when = AdsTag.ReadTaggedAt(p) ?? DateTimeOffset.UtcNow;
                lock (_gate) _favs[p] = when;
                found++;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (found > 0)
        {
            PublishIndex();
            SaveList();
            Changed?.Invoke();
        }
        return found;
    }

    // ------------------------------------------------------------ persistence --

    private void LoadList()
    {
        try
        {
            if (!File.Exists(Paths.Favorites)) return;
            foreach (string line in File.ReadLines(Paths.Favorites))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 1) continue;

                string path = parts[0];
                DateTimeOffset when = DateTimeOffset.UtcNow;
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long ms))
                    when = DateTimeOffset.FromUnixTimeMilliseconds(ms);

                _favs[path] = when;
            }
        }
        catch
        {
            // The list is a cache. Losing it costs the favourites window its contents
            // until the next rescan; the hearts on the files themselves are untouched.
        }
    }

    private void SaveList()
    {
        try
        {
            Paths.Ensure();
            List<KeyValuePair<string, DateTimeOffset>> items;
            lock (_gate) items = _favs.ToList();

            string tmp = Paths.Favorites + ".tmp";
            using (var w = new StreamWriter(tmp, false, new System.Text.UTF8Encoding(false)))
                foreach (var kv in items)
                    w.WriteLine($"{kv.Key}\t{kv.Value.ToUnixTimeMilliseconds()}");

            File.Move(tmp, Paths.Favorites, overwrite: true);
        }
        catch
        {
        }
    }

    /// <summary>Republishes the set of folders the overlay DLL should bother probing.</summary>
    private void PublishIndex()
    {
        List<string> folders = ExpectedFolders();
        _index.Replace(folders);
        // Keep the watchers pointed at exactly the folders we care about. Doing it
        // here means the set can never drift from the index it mirrors.
        _watcher.Watch(folders);
    }

    /// <summary>The folders the index is supposed to contain, right now.</summary>
    private List<string> ExpectedFolders()
    {
        lock (_gate)
            return _favs.Keys
                .Select(p => { try { return Path.GetDirectoryName(p); } catch { return null; } })
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public int IndexedFolderCount => _index.Count;

    public bool IndexHealthy => _index.Healthy;

    /// <summary>
    /// Everything Settings needs to say whether the badge pipeline is actually
    /// working, rather than just how many folders it thinks it has.
    /// </summary>
    public IndexStatus GetStatus()
    {
        var header = _index.ReadHeaderFromDisk();
        int expected = ExpectedFolders().Count;
        int favourites = Count;
        return new IndexStatus(
            Healthy: _index.Healthy && header is not null,
            FoldersOnDisk: header?.Count ?? -1,
            FoldersExpected: expected,
            Favourites: favourites,
            Generation: header?.Generation ?? 0,
            Watching: _watcher.WatchedCount);
    }

    /// <summary>
    /// Compares what the index holds on disk with what it ought to hold, and
    /// republishes if they have drifted apart.
    ///
    /// This exists because the drift has actually happened: the app carried on saving
    /// its favourites list while the index sat on a folder set from hours earlier, and
    /// a folder the index has forgotten is answered with a perfectly legitimate "no
    /// favourites here". Nothing was wrong enough to notice. A cheap periodic check
    /// turns a permanent silent failure into at most a few minutes of one.
    /// </summary>
    public bool CheckIndex(bool repair)
    {
        int expected = ExpectedFolders().Count;
        var header = _index.ReadHeaderFromDisk();

        if (header is null)
        {
            Log.Write("index health: header unreadable");
            if (repair) PublishIndex();
            return false;
        }

        if (header.Value.Count == expected && _index.Healthy) return true;

        Log.Write($"index health: disk holds {header.Value.Count} folder(s), expected {expected}" +
                  (repair ? " - republishing" : ""));
        if (repair) PublishIndex();
        return false;
    }
    // ------------------------------------------------------------ shell redraw --

    /// <summary>
    /// Tells Explorer that these items changed so it re-asks the overlay handler.
    /// Without this the heart does not appear until something else happens to
    /// invalidate the icon cache, which can be minutes.
    /// </summary>
    private static void RefreshIcons(IEnumerable<string> paths)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string p in paths)
        {
            Notify(Native.SHCNE_UPDATEITEM, p);
            try
            {
                string? d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d)) dirs.Add(d);
            }
            catch { }
        }

        foreach (string d in dirs) Notify(Native.SHCNE_UPDATEDIR, d);
    }

    private static void Notify(uint evt, string path)
    {
        IntPtr p = Marshal.StringToCoTaskMemUni(path);
        try
        {
            Native.SHChangeNotify(evt, Native.SHCNF_PATHW | Native.SHCNF_FLUSH, p, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _index.Dispose();
    }
}
