namespace FavExplorer;

internal sealed record PathRename(string OldPath, string NewPath);

/// <summary>
/// Watches the folders that currently hold favourites, so the list stays honest when
/// files are renamed or shuffled around.
///
/// Without this, the tag on the file is always right but our record of *where* it
/// lives decays: rename a photo and the favourites window shows "(missing)" until a
/// manual rescan; move one into a folder that already has favourites and its heart
/// does not appear even though the stream travelled with it.
///
/// Only the folders we already care about are watched — typically a handful — and
/// never recursively. This is deliberately not a filesystem-wide index.
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    // Watching a folder costs a handle and a kernel buffer. A user with favourites
    // scattered across hundreds of folders should not quietly become a resource hog.
    private const int MaxWatchers = 64;

    private const int DebounceMs = 750;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();

    private readonly List<PathRename> _renames = new();
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Threading.Timer _debounce;
    private bool _disposed;

    /// <summary>Renames first, then any other paths worth re-examining.</summary>
    public event Action<IReadOnlyList<PathRename>, IReadOnlyList<string>>? Changed;

    public FolderWatcher()
    {
        _debounce = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int WatchedCount { get { lock (_gate) return _watchers.Count; } }

    /// <summary>Points the watchers at exactly this set of folders, dropping the rest.</summary>
    public void Watch(IEnumerable<string> folders)
    {
        if (_disposed) return;

        var wanted = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(DirIndex.NormalizeFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxWatchers)
            .ToList();

        lock (_gate)
        {
            var current = _watchers.Select(w => DirIndex.NormalizeFolder(w.Path))
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var target = wanted.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (current.SetEquals(target)) return;

            foreach (FileSystemWatcher w in _watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();

            foreach (string folder in wanted)
            {
                // A folder on a drive that has been unplugged, or one that has since
                // been deleted, must not take the whole watcher down with it.
                if (!Directory.Exists(folder)) continue;
                try
                {
                    var w = new FileSystemWatcher(folder)
                    {
                        // Only names matter. We restore timestamps around tagging, so
                        // LastWrite would be both noisy and unreliable here.
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        IncludeSubdirectories = false,
                        InternalBufferSize = 32 * 1024,
                    };
                    w.Renamed += OnRenamed;
                    w.Created += OnCreated;
                    w.Deleted += OnDeleted;
                    w.Error += OnError;
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex)
                {
                    Log.Write($"watcher failed for {folder}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Recorded because a watcher that silently failed to attach is
            // indistinguishable from a folder where nothing ever changes.
            Log.Write($"watching {_watchers.Count} of {wanted.Count} folder(s)" +
                      (_watchers.Count == wanted.Count ? "" : " - some could not be attached"));
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        lock (_gate)
        {
            _renames.Add(new PathRename(e.OldFullPath, e.FullPath));
            Poke();
        }
    }

    // A file arriving in a watched folder may be a favourite that was moved here from
    // somewhere else, carrying its stream with it. Worth a look.
    private void OnCreated(object sender, FileSystemEventArgs e) => Touch(e.FullPath);

    private void OnDeleted(object sender, FileSystemEventArgs e) => Touch(e.FullPath);

    private void Touch(string path)
    {
        lock (_gate)
        {
            _touched.Add(path);
            Poke();
        }
    }

    /// <summary>Restart the quiet period. Bulk operations fire hundreds of events.</summary>
    private void Poke()
    {
        if (_disposed) return;
        try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch { }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Overflowing the kernel buffer means events were dropped, so the only honest
        // response is to admit we no longer know what changed.
        Log.Write($"watcher error: {e.GetException().Message} - a rescan may be needed");
    }

    private void Flush()
    {
        List<PathRename> renames;
        List<string> touched;

        lock (_gate)
        {
            if (_renames.Count == 0 && _touched.Count == 0) return;
            renames = new List<PathRename>(_renames);
            touched = new List<string>(_touched);
            _renames.Clear();
            _touched.Clear();
        }

        try { Changed?.Invoke(renames, touched); }
        catch (Exception ex) { Log.Write($"watcher handler threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            foreach (FileSystemWatcher w in _watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();
        }
        try { _debounce.Dispose(); } catch { }
    }
}
