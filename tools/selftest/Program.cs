using FavExplorer;

// Exercises the C# half of the contract so probe.exe can check the C++ half against
// it. Run this first, then probe.exe on the same paths.
//
//   SelfTest hash <folder>            print the folder hash
//   SelfTest tag <file> [<file>...]   tag files and publish the index
//   SelfTest untag <file> [...]       remove the tags and republish
//   SelfTest status <file> [...]      report whether each file carries the stream
//   SelfTest migrate <file> [...]     move a Heartmark-era tag onto the current stream

string indexPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "FavExplorer", "dirs.bin");

if (args.Length == 0)
{
    Console.WriteLine("usage: SelfTest <hash|tag|untag|status> <path>...");
    return 2;
}

string cmd = args[0].ToLowerInvariant();
string[] rest = args.Skip(1).ToArray();

// The tray app owns dirs.bin and rewrites it wholesale from its own list of
// favourites. If this tool republished the index behind its back, every folder the
// running app knows about would be dropped and the user's hearts would vanish from
// Explorer until the next toggle. Refuse rather than fight over it.
if (cmd is "tag" or "untag" &&
    System.Diagnostics.Process.GetProcessesByName("FavExplorer").Length > 0)
{
    Console.Error.WriteLine(
        "FavExplorer.exe is running and owns the index — quit it from the tray first.\n" +
        "(`hash` and `status` are read-only and safe to run any time.)");
    return 1;
}

if (cmd == "hash")
{
    if (rest.Length == 0) return 2;
    string norm = DirIndex.NormalizeFolder(rest[0]);
    Console.WriteLine($"{norm} -> 0x{DirIndex.HashNormalizedFolder(rest[0]):X16}");
    return 0;
}

if (cmd == "status")
{
    foreach (string p in rest)
    {
        bool ntfs = AdsTag.VolumeSupportsStreams(p);
        bool tagged = AdsTag.IsTagged(p);
        DateTimeOffset? when = tagged ? AdsTag.ReadTaggedAt(p) : null;
        Console.WriteLine($"{p}");
        Console.WriteLine($"  volume takes streams : {ntfs}");
        Console.WriteLine($"  tagged               : {tagged}" +
                          (when is null ? "" : $"  (at {when:u})"));
    }
    return 0;
}

if (cmd == "migrate")
{
    // Touches only the streams on the named files, never the index.
    foreach (string p in rest)
        Console.WriteLine($"{p}: {(AdsTag.MigrateLegacy(p) ? "moved" : "no legacy tag")}");
    return 0;
}

if (cmd == "store")
{
    // Builds the real FavoriteStore - the exact object the tray app runs - and
    // reports what its watcher is actually doing. The tray app must be stopped
    // first: it holds the index file with an exclusive write lock.
    using var store = new FavoriteStore();
    var status = store.GetStatus();
    Console.WriteLine($"favourites : {status.Favourites}");
    Console.WriteLine($"folders    : expected {status.FoldersExpected}, on disk {status.FoldersOnDisk}");
    Console.WriteLine($"watching   : {status.Watching} folder(s)");
    Console.WriteLine($"healthy    : {status.InSync}");
    store.Changed += () => Console.WriteLine($"  CHANGED -> now {store.Count} favourites");
    Console.WriteLine("idling 25s - move or rename something in a watched folder");
    Thread.Sleep(25000);
    Console.WriteLine($"final: {store.Count} favourites");
    return 0;
}

if (cmd == "watch")
{
    // Runs the real FolderWatcher against a folder and prints what it sees, so the
    // watcher can be debugged without reinstalling the tray app.
    var w = new FolderWatcher();
    w.Changed += (renames, touched) =>
    {
        foreach (var r in renames) Console.WriteLine($"  RENAME {r.OldPath}  ->  {r.NewPath}");
        foreach (var t in touched) Console.WriteLine($"  TOUCH  {t}");
    };
    w.Watch(rest);
    Console.WriteLine($"watching {w.WatchedCount} of {rest.Length} folder(s) - 25s");
    foreach (string f in rest) Console.WriteLine($"  {f}  exists={Directory.Exists(f)}");
    Thread.Sleep(25000);
    w.Dispose();
    Console.WriteLine("done");
    return 0;
}

if (cmd == "photos")
{
    // Walks every stage PhotosViewer uses to name the photo on screen, for the
    // window handle given (decimal, as Spy++ or PowerShell print it). Each line is
    // one thing that can fail on its own, so a wrong answer points at its cause.
    if (rest.Length == 0 || !long.TryParse(rest[0], out long raw)) { Console.WriteLine("usage: SelfTest photos <hwnd>"); return 2; }
    var hwnd = new IntPtr(raw);
    Native.GetWindowThreadProcessId(hwnd, out uint pid);
    string? exe = PhotosViewer.ImagePathOf(pid);
    string? cmdLine = PhotosViewer.CommandLineOf(pid);
    string? launched = cmdLine is null ? null : PhotosViewer.ParseLaunchFile(cmdLine);
    string title = PhotosViewer.TitleOf(hwnd);
    string? candidate = launched is null ? null : Path.Combine(Path.GetDirectoryName(launched) ?? "", title);
    Console.WriteLine($"pid            : {pid}");
    Console.WriteLine($"exe            : {exe ?? "(OpenProcess/QueryFullProcessImageName failed)"}");
    Console.WriteLine($"viewer window  : {PhotosViewer.IsViewerWindow(hwnd)}");
    Console.WriteLine($"command line   : {cmdLine ?? "(NtQueryInformationProcess failed)"}");
    Console.WriteLine($"launch file    : {launched ?? "(not parsed)"}");
    Console.WriteLine($"title          : [{title}]");
    Console.WriteLine($"current path   : {candidate ?? "(n/a)"}  exists={candidate is not null && File.Exists(candidate)}");
    return 0;
}

if (cmd is not ("tag" or "untag")) { Console.WriteLine("unknown command"); return 2; }

bool tagging = cmd == "tag";
var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (string p in rest)
{
    if (!File.Exists(p) && !Directory.Exists(p)) { Console.WriteLine($"missing: {p}"); continue; }
    if (!AdsTag.VolumeSupportsStreams(p)) { Console.WriteLine($"not NTFS: {p}"); continue; }

    bool ok = tagging ? AdsTag.Tag(p) : AdsTag.Untag(p);
    Console.WriteLine($"{(tagging ? "tagged" : "untagged")} {(ok ? "ok  " : "FAIL")}  {p}");

    string? d = Path.GetDirectoryName(p);
    if (tagging && ok && !string.IsNullOrEmpty(d)) folders.Add(d);
}

using var index = new DirIndex(indexPath);
index.Replace(folders);
Console.WriteLine($"\nindex: {index.Count} folder(s), generation {index.Generation}");
foreach (string f in folders)
    Console.WriteLine($"  0x{DirIndex.HashNormalizedFolder(f):X16}  {f}");

return 0;
