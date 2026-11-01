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
    private FavoritesForm? _favorites;
    private SettingsForm? _settingsForm;

    private ToolStripMenuItem _miToggle = null!;
    private ToolStripMenuItem _miPause = null!;
    private ToolStripMenuItem _miStatus = null!;

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

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(),
            Text = "Fav Explorer",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowFavorites();

        _store.Changed += () => _marshal.BeginInvoke(UpdateMenuText);

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
    }

    // ------------------------------------------------------------------- menu --

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = true };

        _miStatus = new ToolStripMenuItem("Fav Explorer") { Enabled = false };

        _miToggle = new ToolStripMenuItem("Favourite selected files", null, (_, _) => OnHotkey())
        {
            ShortcutKeyDisplayString = _settings.Shortcut.ToString(),
        };

        var miShow = new ToolStripMenuItem("Show all favourites…", null, (_, _) => ShowFavorites());
        var miRescan = new ToolStripMenuItem("Rescan a folder…", null, (_, _) => RescanFolder());

        _miPause = new ToolStripMenuItem("Pause the shortcut", null, (_, _) => TogglePause())
        {
            CheckOnClick = true,
        };

        var miSettings = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
        var miQuit = new ToolStripMenuItem("Quit Fav Explorer", null, (_, _) => Quit());

        menu.Items.AddRange(new ToolStripItem[]
        {
            _miStatus,
            new ToolStripSeparator(),
            _miToggle,
            miShow,
            miRescan,
            new ToolStripSeparator(),
            _miPause,
            miSettings,
            new ToolStripSeparator(),
            miQuit,
        });
        return menu;
    }

    private void UpdateMenuText()
    {
        int n = _store.Count;
        _miStatus.Text = n == 1 ? "1 favourite" : $"{n:N0} favourites";
        _miToggle.ShortcutKeyDisplayString = _settings.Shortcut.ToString();
        _tray.Text = $"Fav Explorer — {_miStatus.Text}";
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

    private void TogglePause()
    {
        _hook.Paused = _miPause.Checked;
        _tray.Text = _hook.Paused ? "Fav Explorer — shortcut paused" : $"Fav Explorer — {_store.Count:N0} favourites";
    }

    // ------------------------------------------------------------------ views --

    private void ShowFavorites()
    {
        if (_favorites is null || _favorites.IsDisposed)
            _favorites = new FavoritesForm(_store);

        _favorites.Show();
        _favorites.WindowState = FormWindowState.Normal;
        _favorites.Activate();
    }

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings, _store, applied =>
            {
                _hook.Rebind(applied.Shortcut);
                UpdateMenuText();
            });
        }

        _settingsForm.Show();
        _settingsForm.WindowState = FormWindowState.Normal;
        _settingsForm.Activate();
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

        _tray.BalloonTipTitle = "The heart won't show yet";
        _tray.BalloonTipText = detail;
        _tray.BalloonTipIcon = ToolTipIcon.Warning;
        _tray.ShowBalloonTip(8000);
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
            _indexWatchdog.Dispose();
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
