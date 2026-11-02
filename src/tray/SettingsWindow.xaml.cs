using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
// WinForms and System.Drawing are global usings for the rest of the app. In this
// file the shared names mean the WPF types, except Keys, which Hotkey speaks.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace FavExplorer;

internal sealed record HandlerRow(int Rank, string Name, bool Ours, bool Honoured);

internal partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly FavoriteStore _store;
    private readonly Action<Settings> _onApplied;
    private Hotkey _pending;

    public SettingsWindow(Settings settings, FavoriteStore store, Action<Settings> onApplied)
    {
        _settings = settings;
        _store = store;
        _onApplied = onApplied;
        _pending = settings.Shortcut;
        InitializeComponent();

        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            AppIcon.Load().Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);

        IsVisibleChanged += (_, _) => { if (IsVisible) LoadValues(); };
    }

    public void Present()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void LoadValues()
    {
        _pending = _settings.Shortcut;
        ShowShortcut();
        CaptureError.Visibility = Visibility.Collapsed;
        ToastBox.IsChecked = _settings.ShowToast;
        StartupBox.IsChecked = Settings.IsRegisteredAtStartup();
        RefreshDiagnostics();
        SaveButton.Focus();
    }

    // ------------------------------------------------------------ shortcut --

    private void ShowShortcut() => KeyCaps.ItemsSource = _pending.ToString().Split('+');

    private void OnCaptureClick(object sender, MouseButtonEventArgs e)
    {
        Capture.Focus();
        e.Handled = true;
    }

    private void OnCaptureFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        KeyCaps.Visibility = Visibility.Collapsed;
        CaptureHint.Visibility = Visibility.Visible;
        Capture.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
    }

    private void OnCaptureBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        KeyCaps.Visibility = Visibility.Visible;
        CaptureHint.Visibility = Visibility.Collapsed;
        Capture.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        ShowShortcut();
    }

    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;

        // Plain Tab and Escape still move around the window, or nobody using the
        // keyboard could ever leave the box.
        if (mods == ModifierKeys.None && key is Key.Tab or Key.Escape)
        {
            if (key == Key.Escape) { SaveButton.Focus(); e.Handled = true; }
            return;
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return; // still holding modifiers down

        var candidate = new Hotkey(
            mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt),
            (Keys)KeyInterop.VirtualKeyFromKey(key));

        if (mods == ModifierKeys.None)
        {
            ShowCaptureError("The shortcut needs at least one of Ctrl, Shift or Alt. A bare letter would " +
                             "break type-to-select in Explorer, where typing F jumps to the first file starting with F.");
            return;
        }

        if (candidate.IsDangerous)
        {
            ShowCaptureError($"{candidate} already means something destructive or surprising in File Explorer — " +
                             "Ctrl+D deletes the selection, for instance. Pick a combination with Shift or Alt in it.");
            return;
        }

        _pending = candidate;
        CaptureError.Visibility = Visibility.Collapsed;
        SaveButton.Focus();
    }

    private void ShowCaptureError(string text)
    {
        CaptureError.Text = text;
        CaptureError.Visibility = Visibility.Visible;
    }

    private void OnDefaultShortcut(object sender, RoutedEventArgs e)
    {
        _pending = Hotkey.Default;
        CaptureError.Visibility = Visibility.Collapsed;
        ShowShortcut();
    }

    // ----------------------------------------------------------- diagnosis --

    private void RefreshDiagnostics()
    {
        OverlayStatus s = OverlayDiagnostics.Read();

        SlotHeadline.Text = s.Headline;
        SetState(SlotGlyph, s.HasSlot);
        SlotDetail.Text = !s.Registered
            ? "Run install.ps1 once, as an administrator, to register the handler with Windows."
            : !s.DllPresent
            ? $"Windows is pointed at {s.DllPath}, but nothing is there. Re-run install.ps1."
            : s.HasSlot
            ? $"Ranked {s.Position} of {s.Total} registered handlers, inside the {s.UsableSlots} Windows actually honours."
            : $"Ranked {s.Position} of {s.Total}, outside the {s.UsableSlots} Windows honours. Tagging still works and the tags are safe — but no heart will be drawn until one of the handlers above Fav Explorer is removed.";

        // The folder index is the other half of the pipeline, and the half that has
        // actually failed in practice. A folder it has forgotten produces a perfectly
        // legitimate "no favourites here", so the only way to know it is wrong is to
        // compare it against what it ought to hold and say so out loud.
        IndexStatus ix = _store.GetStatus();
        SetState(IndexGlyph, ix.InSync);
        if (ix.InSync)
        {
            IndexHeadline.Text = "Index healthy";
            IndexDetail.Text = $"{Count(ix.Favourites, "favourite")} across {Count(ix.FoldersOnDisk, "folder")}, " +
                               $"{ix.Watching:N0} watched for changes.";
        }
        else if (ix.FoldersOnDisk < 0)
        {
            IndexHeadline.Text = "Index unreadable";
            IndexDetail.Text = "Hearts will not be drawn until it is repaired.";
        }
        else
        {
            IndexHeadline.Text = "Index out of step";
            IndexDetail.Text = $"It holds {Count(ix.FoldersOnDisk, "folder")} but should hold {ix.FoldersExpected:N0}, " +
                               "so some hearts may be missing. Repair fixes it.";
        }

        Handlers.ItemsSource = OverlayDiagnostics.ListHandlers()
            .Select((h, i) => new HandlerRow(i + 1, h.Name.Trim(), h.Ours, h.Honoured))
            .ToList();
    }

    private static void SetState(System.Windows.Controls.TextBlock glyph, bool good)
    {
        // A tick when it works, a warning sign when it doesn't, in the theme's own
        // success and critical colours.
        glyph.Text = good ? "" : "";
        glyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            good ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    private void OnRepair(object sender, RoutedEventArgs e)
    {
        _store.CheckIndex(repair: true);
        RefreshDiagnostics();
    }

    // -------------------------------------------------------------- buttons --

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.Shortcut = _pending;
        _settings.ShowToast = ToastBox.IsChecked == true;
        _settings.RunAtStartup = StartupBox.IsChecked == true;
        _settings.Save();

        try { Settings.SetStartup(_settings.RunAtStartup); } catch { }

        _onApplied(_settings);
        Hide();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Hide();
}
