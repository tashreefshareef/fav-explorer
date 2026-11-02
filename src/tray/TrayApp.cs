using System.Windows.Forms;

namespace FavExplorer;

internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly FavoriteStore _store;
    private readonly HotkeyHook _hook;
    private readonly NotifyIcon _tray;
    private readonly Control _marshal;

    // The index has gone stale in the field once already, silently. A periodic check
    // costs one header read and bounds how long that can go unnoticed.
    private readonly System.Windows.Forms.Timer _indexWatchdog = new();

    // Wakes a few times a day to see whether the weekly update check is due; it
    // does nothing at all unless the user turned update checks on.
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };

    // Where a click on the current balloon should go, if anywhere.
    private string? _balloonUrl;
    private FavoritesWindow? _favorites;
    private SettingsWindow? _settingsWindow;

    private readonly TrayMenu _menu;

    // A second launch sets this rather than starting a second copy; see Program.
    private readonly EventWaitHandle _showRequest = new(false, EventResetMode.AutoReset, Program.ShowEvent);
    private readonly RegisteredWaitHandle _showWait;

    public TrayApp()
    {
        _settings = Settings.Load();
        _store = new FavoriteStore();

        // A hidden window is the only reliable way to get work from the hook
        // callback onto the UI thread. Creating the handle up front means the very
        // first keystroke doesn't race the window creation.
        _marshal = new Control();
        _marshal.CreateControl();
        _ = _marshal.Handle;

        _hook = new HotkeyHook(_marshal, _settings.Shortcut, OnHotkey);

        _menu = new TrayMenu(
            toggle: OnHotkey,
            showAll: ShowFavorites,
            rescan: RescanFolder,
            pause: paused => SetPaused(paused),
            settings: ShowSettings,
            quit: Quit);

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(),
            Text = "Fav Explorer",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFavorites();
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) _menu.Show(); };
        _tray.BalloonTipClicked += (_, _) => { if (_balloonUrl is string url) OpenUrl(url); };

        _store.Changed += () => _marshal.BeginInvoke(UpdateMenuText);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showRequest,
            (_, _) => _marshal.BeginInvoke(ShowFavorites), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!_hook.Install())
        {
            MessageBox.Show(
                "Fav Explorer couldn't register its keyboard shortcut, so the hotkey won't work.\n\n" +
                "This usually means another copy is already running.",
                "Fav Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        WarnIfOverlayUnavailable();
        UpdateMenuText();

        // Stale entries are cheap to find and confusing to leave lying around, but
        // there's no reason to make the user wait for the check at startup.
        Task.Run(() => _store.Reconcile());

        _indexWatchdog.Interval = 5 * 60 * 1000;
        _indexWatchdog.Tick += (_, _) => Task.Run(() =>
        {
            bool wasHealthy = _store.IndexHealthy;
            if (_store.CheckIndex(repair: true)) return;
            // Only shout if this is a new problem — a repaired drift is not worth a
            // notification every five minutes.
            if (wasHealthy) _marshal.BeginInvoke(() => UpdateMenuText());
        });
        _indexWatchdog.Start();

        // And once at startup, after the reconcile has had a chance to settle.
        Task.Run(async () => { await Task.Delay(4000); _store.CheckIndex(repair: true); });

        _updateTimer.Tick += (_, _) => CheckForUpdates();
        _updateTimer.Start();
        // Not at the moment of sign-in, when everything else is starting too.
        Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => _marshal.BeginInvoke(CheckForUpdates));
    }

    // ------------------------------------------------------------------- menu --

    private void UpdateMenuText()
    {
        int n = _store.Count;
        string count = n == 1 ? "1 favourite" : $"{n:N0} favourites";
        _menu.Status.Header = count;
        _menu.Toggle.InputGestureText = _settings.Shortcut.ToString();
        _tray.Text = _hook.Paused ? "Fav Explorer — shortcut paused" : $"Fav Explorer — {count}";
    }

    // ------------------------------------------------------------- the action --

    private void OnHotkey()
    {
        List<string> selection;
        if (PhotosViewer.IsForeground())
        {
            string? shown = PhotosViewer.GetCurrentPath();
            // This path is inferred from a window title and a command line rather
            // than asked of the shell, so leave a trace of what it concluded.
            Log.Write($"shortcut in Photos: {shown ?? "could not resolve the photo on screen"}");
            if (shown is null)
            {
                if (_settings.ShowToast) Toast.Show("Couldn't tell which photo Photos is showing");
                return;
            }
            selection = new List<string> { shown };
        }
        else
        {
            selection = ExplorerSelection.GetSelectedPaths();
            if (selection.Count == 0)
            {
                if (_settings.ShowToast) Toast.Show("Nothing selected in Explorer");
                return;
            }
        }

        ToggleResult r = _store.Toggle(selection);
        if (r.Touched == 0)
            Log.Write($"shortcut tagged nothing: {selection.Count} selected, {r.Skipped} skipped" +
                      (r.SkipReason is null ? "" : $" ({r.SkipReason})"));
        if (!_settings.ShowToast) return;

        if (r.Touched == 0)
        {
            Toast.Show(r.SkipReason is null
                ? "Couldn't tag that selection"
                : $"Couldn't tag that — {r.SkipReason}");
            return;
        }

        string msg;
        if (r.Added > 0)
        {
            // Naming the file is much clearer than a count when there's only one —
            // but a folder can be tagged too, so don't assume File.Exists holds.
            string? only = selection.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
            msg = r.Added == 1 && only is not null
                ? $"Favourited {Path.GetFileName(only.TrimEnd('\\'))}"
                : $"Favourited {r.Added} items";
        }
        else
            msg = r.Removed == 1 ? "Removed from favourites" : $"Removed {r.Removed} favourites";

        if (r.Skipped > 0) msg += $" · {r.Skipped} skipped";
        Toast.Show(msg);
    }

    private void SetPaused(bool paused)
    {
        _hook.Paused = paused;
        UpdateMenuText();
    }

    // ------------------------------------------------------------------ views --

    private void ShowFavorites()
    {
        // Never closed, only hidden, so one instance lasts the life of the app.
        _favorites ??= new FavoritesWindow(_store, _settings);
        _favorites.Present();
    }

    private void ShowSettings()
    {
        _settingsWindow ??= new SettingsWindow(_settings, _store, applied =>
        {
            _hook.Rebind(applied.Shortcut);
            UpdateMenuText();
            // Turning update checks on should check now, not up to six hours from now.
            CheckForUpdates();
        });
        _settingsWindow.Present();
    }

    private void RescanFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Pick a folder to scan for photos that already carry the tag.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        string folder = dlg.SelectedPath;
        Toast.Show("Scanning…");

        Task.Run(() =>
        {
            int found = _store.Rescan(folder, recursive: true);
            _marshal.BeginInvoke(() => Toast.Show(
                found == 0 ? "No tagged files found there"
                           : found == 1 ? "Recovered 1 favourite" : $"Recovered {found} favourites"));
        });
    }

    private void WarnIfOverlayUnavailable()
    {
        OverlayStatus s = OverlayDiagnostics.Read();
        if (s.HasSlot) return;

        string detail = !s.Registered
            ? "The overlay handler isn't registered yet. Run install.ps1 to finish setting up."
            : !s.DllPresent
            ? "The overlay handler is registered but HeartOverlay.dll is missing from where it was installed."
            : $"Windows honours about {s.UsableSlots} icon overlay handlers and Fav Explorer is ranked " +
              $"{s.Position} of {s.Total}. Tagging still works, but the heart won't be drawn until " +
              "another handler is removed. Settings shows the full list.";

        _balloonUrl = null;
        _tray.BalloonTipTitle = "The heart won't show yet";
        _tray.BalloonTipText = detail;
        _tray.BalloonTipIcon = ToolTipIcon.Warning;
        _tray.ShowBalloonTip(8000);
    }

    // --------------------------------------------------------------- updates --

    /// <summary>
    /// The weekly look at GitHub, if the user turned it on. A newer version is
    /// announced once, in a balloon that opens its release page when clicked.
    /// </summary>
    private async void CheckForUpdates()
    {
        if (!UpdateCheck.IsDue(_settings)) return;

        UpdateInfo? info = await UpdateCheck.FetchAsync();
        if (info is null) return; // offline or GitHub unreachable: try again next tick

        _settings.LastUpdateCheck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string latest = info.Latest.ToString();
        bool announce = info.IsNewer && _settings.AnnouncedVersion != latest;
        if (announce) _settings.AnnouncedVersion = latest;
        _settings.Save();
        Log.Write($"update check: latest {latest}, running {UpdateCheck.Current}");
        if (!announce) return;

        _balloonUrl = info.Url;
        _tray.BalloonTipTitle = $"Fav Explorer {latest} is available";
        _tray.BalloonTipText = $"You have {UpdateCheck.Current}. Click to see what's new and download it.";
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(10000);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ------------------------------------------------------------------- exit --

    private void Quit()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait.Unregister(null);
            _showRequest.Dispose();
            _indexWatchdog.Dispose();
            _updateTimer.Dispose();
            _hook.Dispose();
            _tray.Dispose();
            _store.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Load()
    {
        if (_cached is not null) return _cached;
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return _cached = extracted;
            }
        }
        catch
        {
        }
        return _cached = SystemIcons.Application;
    }
}
