using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// WinForms and System.Drawing are global usings for the rest of the app. In this
// file the shared names mean the WPF types.
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace FavExplorer;

/// <summary>
/// Every favourite in one place, viewed the ways Explorer views a folder, with a way
/// back to the file.
///
/// This is the answer to "I hearted it three months ago, where is it now" — the
/// question the hearts on the thumbnails cannot answer on their own.
/// </summary>
internal partial class FavoritesWindow : Window
{
    // Past this the off-screen thumbnails seen longest ago are let go. The shell keeps
    // its own cache, so getting one back later is cheap.
    private const long ThumbBudget = 192L * 1024 * 1024;

    // Explorer opens at most this many at once without asking; there is no good
    // reason to open more photos than that from here.
    private const int OpenLimit = 15;

    public static readonly DependencyProperty ThumbBoxProperty = DependencyProperty.Register(
        nameof(ThumbBox), typeof(double), typeof(FavoritesWindow), new PropertyMetadata(160.0));

    /// <summary>The thumbnail size of the current view, in device-independent pixels.</summary>
    public double ThumbBox
    {
        get => (double)GetValue(ThumbBoxProperty);
        set => SetValue(ThumbBoxProperty, value);
    }

    private readonly FavoriteStore _store;
    private readonly Settings _settings;
    private readonly ObservableCollection<FavItem> _items = new();
    private readonly Dictionary<string, FavItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListCollectionView _view;
    private readonly ShellThumbnails _thumbs;
    private readonly DispatcherTimer _thumbPass;
    private readonly DispatcherTimer _noticeTimer;

    private FavView _mode;
    private FavSort _sort;
    private bool _descending;
    private FavGroup _group;
    private string _filter = "";
    private string? _notice;
    private string? _lastFolder;
    private bool _toTop;

    private bool _reloading, _reloadAgain;
    private long _thumbBytes;
    private long _clock;

    private Point? _dragFrom;
    private FavItem? _collapseTo;

    public FavoritesWindow(FavoriteStore store, Settings settings)
    {
        _store = store;
        _settings = settings;
        InitializeComponent();

        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            AppIcon.Load().Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        // The message loop belongs to WinForms; without this, keys meant for this
        // window never get WPF's keyboard handling.
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);

        _mode = Enum.TryParse(settings.FavouritesView, out FavView v) ? v : FavView.Large;
        _sort = Enum.TryParse(settings.FavouritesSort, out FavSort s) ? s : FavSort.Added;
        _descending = settings.FavouritesSortDescending;
        _group = Enum.TryParse(settings.FavouritesGroup, out FavGroup g) ? g : FavGroup.Date;

        _view = new ListCollectionView(_items) { Filter = Matches };
        Favs.ItemsSource = _view;
        Favs.SelectionChanged += (_, _) => UpdateStatus();
        Favs.ContextMenu = BuildItemMenu();
        Favs.ContextMenuOpening += (_, e) => { if (Favs.SelectedItems.Count == 0) e.Handled = true; };
        Favs.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueThumbnails()));
        Favs.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));

        _thumbs = new ShellThumbnails(Dispatcher, OnThumbnail);
        _thumbPass = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        _thumbPass.Tick += (_, _) => { _thumbPass.Stop(); RequestThumbnails(); };
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice = null; UpdateStatus(); };

        _store.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Reload(); });
        IsVisibleChanged += (_, _) =>
        {
            // Hidden, the thumbnails are only memory; they come back from the shell's
            // cache in moments. Shown, the list may be days old, and so may "Today".
            if (IsVisible) { _toTop = true; Reload(); }
            else DropThumbnails();
        };

        ApplyView(save: false);
        ApplySort(save: false);

        // The default size is a ceiling, not a promise: on a small laptop screen
        // 1080x720 would run off the bottom.
        Rect work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width * 0.9);
        Height = Math.Min(Height, work.Height * 0.9);
        if (settings.FavouritesPlacement is not null) WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => RestorePlacement();
        SizeChanged += (_, e) => { if (e.WidthChanged) FitCommandBar(); };
        Loaded += (_, _) => FitCommandBar();
    }

    // ----------------------------------------------------------- placement --

    private void RestorePlacement()
    {
        string[]? parts = _settings.FavouritesPlacement?.Split(',');
        if (parts is not { Length: 5 } || !parts.All(p => int.TryParse(p, out _))) return;
        int[] n = parts.Select(int.Parse).ToArray();

        var wp = new Native.WINDOWPLACEMENT
        {
            length = System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>(),
            // Coming back minimized would just look like the window failed to open.
            showCmd = n[0] == Native.SW_SHOWMINIMIZED ? Native.SW_SHOWNORMAL : n[0],
            rcNormalPosition = new Native.RECT { Left = n[1], Top = n[2], Right = n[3], Bottom = n[4] },
        };
        Native.SetWindowPlacement(new WindowInteropHelper(this).Handle, ref wp);
    }

    private void SavePlacement()
    {
        var wp = new Native.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(new WindowInteropHelper(this).Handle, ref wp)) return;
        Native.RECT r = wp.rcNormalPosition;
        _settings.FavouritesPlacement = $"{wp.showCmd},{r.Left},{r.Top},{r.Right},{r.Bottom}";
        _settings.Save();
    }

    // --------------------------------------------------------- command bar --

    // 0: everything with labels. 1: icons only. 2: icons only, and the move, copy
    // and share buttons live in the ⋯ menu instead.
    private int _barLevel = -1;

    private void FitCommandBar()
    {
        // Measured against the window, not the bar: a panel whose content is wider
        // than its slot reports the content's width, so the bar itself never looks
        // too narrow for its buttons.
        if (Content is not FrameworkElement root || root.ActualWidth <= 0) return;

        // Hiding a label doesn't change the bar's measured width until the next
        // layout pass, so measuring after each step reads the old width. Instead the
        // width of each level is worked out once, while everything is still showing.
        if (_barWidths is null)
        {
            if (_barLevel != -1 || BarButtons.DesiredSize.Width <= 0) return;
            static double W(FrameworkElement e) => e.DesiredSize.Width; // includes margins
            double full = W(BarButtons);
            double labels = new[] { OpenLabel, RevealLabel, MoveLabel, CopyToLabel, SortLabel, ViewLabel }.Sum(W);
            double extras = new FrameworkElement[] { MoveButton, CopyToButton, ActionsDivider, CopyButton, ShareButton }.Sum(W)
                            - W(MoveLabel) - W(CopyToLabel);
            _barWidths = new[] { full, full - labels, full - labels - extras };
        }

        const double SearchRoom = 150 + 12; // the search box's MinWidth and margin
        double room = root.ActualWidth - CommandBar.Margin.Left - CommandBar.Margin.Right - SearchRoom;
        int level = 0;
        while (level < 2 && _barWidths[level] > room) level++;
        SetBarLevel(level);
    }

    private double[]? _barWidths;

    private void SetBarLevel(int level)
    {
        if (level == _barLevel) return;
        _barLevel = level;

        Visibility labels = level == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (TextBlock label in new[] { OpenLabel, RevealLabel, MoveLabel, CopyToLabel, SortLabel, ViewLabel })
            label.Visibility = labels;

        Visibility extras = level < 2 ? Visibility.Visible : Visibility.Collapsed;
        foreach (UIElement e in new UIElement[] { MoveButton, CopyToButton, ActionsDivider, CopyButton, ShareButton })
            e.Visibility = extras;
    }

    public void Present()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Quitting is the tray icon's job. Closing this window just puts it away,
        // remembering where it was for next time.
        e.Cancel = true;
        SavePlacement();
        Hide();
    }

    // ------------------------------------------------------------- loading --

    private readonly record struct Probed(string Path, DateTimeOffset Added, bool Missing, long? Size);

    private async void Reload()
    {
        if (_reloading) { _reloadAgain = true; return; }
        _reloading = true;
        try
        {
            do
            {
                _reloadAgain = false;
                // Touching every file is a disk access each, and some of them may be on
                // a drive that has to spin up or a share that has gone away.
                List<Probed> probed = await Task.Run(() => _store.Snapshot().Select(Probe).ToList());
                Apply(probed);
            } while (_reloadAgain);
        }
        finally
        {
            _reloading = false;
        }
    }

    private static Probed Probe(FavoriteEntry e)
    {
        try
        {
            var fi = new FileInfo(e.Path);
            if (fi.Exists) return new Probed(e.Path, e.AddedUtc, false, fi.Length);
            if (Directory.Exists(e.Path)) return new Probed(e.Path, e.AddedUtc, false, null);
        }
        catch
        {
        }
        return new Probed(e.Path, e.AddedUtc, true, null);
    }

    /// <summary>
    /// Updates the existing items in place rather than starting over, so selection,
    /// scroll position and thumbnails all survive a favourite being added elsewhere.
    /// </summary>
    private void Apply(List<Probed> probed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime now = DateTime.Now;

        // Not inside DeferRefresh: the view refuses changes to its source while a
        // refresh is deferred.
        foreach (Probed p in probed)
        {
            seen.Add(p.Path);
            if (!_byPath.TryGetValue(p.Path, out FavItem? item))
            {
                item = new FavItem(p.Path) { Added = p.Added };
                FavGrouping.Assign(item, _group, now);
                _byPath.Add(p.Path, item);
                _items.Add(item);
            }
            item.Added = p.Added;
            item.Missing = p.Missing;
            item.Size = p.Size;
            FavGrouping.Assign(item, _group, now);
        }

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            FavItem item = _items[i];
            if (seen.Contains(item.Path)) continue;
            _thumbBytes -= item.ThumbBytes;
            item.DropThumb();
            _byPath.Remove(item.Path);
            _items.RemoveAt(i);
        }

        // Dates and groups may have moved under existing items; the view only places
        // an item when it arrives, so put everything back in order once.
        _view.Refresh();

        UpdateStatus();
        QueueThumbnails();

        // Opening the window starts at the top, newest first, as an Explorer window
        // does. Only after layout, which is what would otherwise leave it part way down.
        if (_toTop)
        {
            _toTop = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Scroller(Favs)?.ScrollToHome());
        }
    }

    private static ScrollViewer? Scroller(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer sv) return sv;
            if (Scroller(child) is ScrollViewer inner) return inner;
        }
        return null;
    }

    // ------------------------------------------------------ view, sort, group --

    private void SetView(FavView mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        ApplyView(save: true);
    }

    private void ApplyView(bool save)
    {
        ThumbBox = _mode switch
        {
            FavView.Details => 24,
            FavView.Tiles => 64,
            FavView.Medium => 96,
            FavView.Large => 160,
            _ => 256,
        };

        bool details = _mode == FavView.Details;
        Favs.View = details ? (ViewBase)Resources["DetailsView"] : null;
        Favs.ItemTemplate = details ? null : (DataTemplate)Resources[_mode == FavView.Tiles ? "TileTemplate" : "IconTemplate"];
        Favs.ItemContainerStyle = (Style)Resources[details ? "RowItemStyle" : "CardItemStyle"];
        Favs.ItemsPanel = (ItemsPanelTemplate)Resources[details ? "StackPanel" : "WrapPanel"];
        // Icons wrap to the window's width instead of running off to the right.
        ScrollViewer.SetHorizontalScrollBarVisibility(Favs, details ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);

        Highlight(DetailsButton, details);
        Highlight(IconsButton, _mode == FavView.Large);

        if (save)
        {
            _settings.FavouritesView = _mode.ToString();
            _settings.Save();
        }
        QueueThumbnails();
    }

    private static void Highlight(Button b, bool on)
    {
        if (on) b.SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        else b.ClearValue(BackgroundProperty);
    }

    private void SetSort(FavSort sort, bool descending)
    {
        _sort = sort;
        _descending = descending;
        ApplySort(save: true);
    }

    private void SetGroup(FavGroup group)
    {
        _group = group;
        ApplySort(save: true);
    }

    // Newest and largest first is what anyone sorting by those wants to see.
    private static bool StartsDescending(FavSort sort) => sort is FavSort.Added or FavSort.Size;

    private void ApplySort(bool save)
    {
        DateTime now = DateTime.Now;
        foreach (FavItem i in _items) FavGrouping.Assign(i, _group, now);

        using (_view.DeferRefresh())
        {
            _view.GroupDescriptions.Clear();
            if (_group != FavGroup.None)
                _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FavItem.GroupName)));
            _view.CustomSort = new FavComparer(_sort, _descending, _group);
        }

        ShowSortArrow();

        if (save)
        {
            _settings.FavouritesSort = _sort.ToString();
            _settings.FavouritesSortDescending = _descending;
            _settings.FavouritesGroup = _group.ToString();
            _settings.Save();
        }
        QueueThumbnails();
    }

    private bool Matches(object o)
    {
        if (_filter.Length == 0) return true;
        var i = (FavItem)o;
        return i.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
            || i.Folder.Contains(_filter, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateStatus();
        QueueThumbnails();
    }

    private void UpdateStatus()
    {
        int total = _items.Count, shown = _view.Count, selected = Favs.SelectedItems.Count;
        int folders = _items.Select(i => i.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        string text = total == 0 ? "No favourites yet"
            : shown == total ? $"{Count(total, "favourite")} in {Count(folders, "folder")}"
            : $"{shown:N0} of {Count(total, "favourite")}";
        if (selected > 0) text += $"      {selected:N0} selected";
        if (_notice is not null) text += $"      {_notice}";
        StatusText.Text = text;

        EmptyText.Text = total == 0
            ? $"Nothing hearted yet.\n\nSelect photos in File Explorer and press {_settings.Shortcut}."
            : $"No favourites match “{_filter}”";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool any = selected > 0;
        OpenButton.IsEnabled = RevealButton.IsEnabled = MoveButton.IsEnabled = CopyToButton.IsEnabled =
            CopyButton.IsEnabled = ShareButton.IsEnabled = RemoveButton.IsEnabled = any;
    }

    private void Notice(string text)
    {
        _notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
        UpdateStatus();
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    // ---------------------------------------------------------- thumbnails --

    private void QueueThumbnails()
    {
        if (!_thumbPass.IsEnabled) _thumbPass.Start();
    }

    /// <summary>
    /// Asks for thumbnails for what is on screen, then for about a screen either
    /// side, so ordinary scrolling finds them already there.
    /// </summary>
    private void RequestThumbnails()
    {
        if (!IsVisible || Favs.ActualHeight <= 0) return;

        double h = Favs.ActualHeight;
        var onScreen = new List<(FavItem Item, Rect At)>();
        var nearby = new List<(FavItem Item, Rect At)>();

        foreach (ListViewItem box in Containers(Favs))
        {
            if (box.DataContext is not FavItem item || box.RenderSize.IsEmpty) continue;
            Rect at;
            try { at = box.TransformToAncestor(Favs).TransformBounds(new Rect(box.RenderSize)); }
            catch (InvalidOperationException) { continue; }

            if (at.Bottom < -h || at.Top > 2 * h) continue;
            item.LastSeen = ++_clock;
            (at.Bottom >= 0 && at.Top <= h ? onScreen : nearby).Add((item, at));
        }

        int px = (int)Math.Ceiling(ThumbBox * VisualTreeHelper.GetDpi(this).DpiScaleX);
        List<FavItem> wanted = onScreen.OrderBy(t => t.At.Top).ThenBy(t => t.At.Left)
            .Concat(nearby.OrderBy(t => Math.Abs(t.At.Top - h / 2)))
            .Select(t => t.Item)
            .Where(i => i.ThumbPixels < px)
            .ToList();
        _thumbs.Request(wanted, px);

        if (_thumbBytes > ThumbBudget)
        {
            var keep = new HashSet<FavItem>(onScreen.Concat(nearby).Select(t => t.Item));
            foreach (FavItem old in _items.Where(i => i.ThumbBytes > 0 && !keep.Contains(i)).OrderBy(i => i.LastSeen).ToList())
            {
                _thumbBytes -= old.ThumbBytes;
                old.DropThumb();
                if (_thumbBytes < ThumbBudget * 3 / 4) break;
            }
        }
    }

    /// <summary>The item containers that exist right now, without walking into them.</summary>
    private static IEnumerable<ListViewItem> Containers(DependencyObject node)
    {
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is ListViewItem box) yield return box;
            else foreach (ListViewItem inner in Containers(child)) yield return inner;
        }
    }

    private void OnThumbnail(FavItem item, BitmapSource? bmp, int px)
    {
        // Gone from the list, hidden since, or overtaken by a bigger request.
        if (!IsVisible || !_byPath.ContainsKey(item.Path) || px < item.ThumbPixels) return;
        _thumbBytes -= item.ThumbBytes;
        item.SetThumb(bmp, px);
        _thumbBytes += item.ThumbBytes;
    }

    private void DropThumbnails()
    {
        _thumbs.Request(Array.Empty<FavItem>(), 0);
        foreach (FavItem i in _items) i.DropThumb();
        _thumbBytes = 0;
    }

    // ------------------------------------------------------------- actions --

    private List<FavItem> Selection() => Favs.SelectedItems.Cast<FavItem>().ToList();

    private void OpenItems(IEnumerable<FavItem> items)
    {
        foreach (FavItem i in items.Where(i => !i.Missing).Take(OpenLimit))
        {
            try { Process.Start(new ProcessStartInfo(i.Path) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void RevealSelected()
    {
        FavItem? first = Selection().FirstOrDefault();
        if (first is not null) ExplorerSelection.RevealInExplorer(first.Path);
    }

    /// <summary>
    /// Copies the files themselves, so pasting into a folder copies them there, and
    /// their paths as text for anywhere that only takes text.
    /// </summary>
    private void CopySelected(bool pathsOnly)
    {
        List<FavItem> items = Selection();
        if (items.Count == 0) return;

        var data = new DataObject();
        data.SetText(string.Join(Environment.NewLine, items.Select(i => i.Path)));
        if (!pathsOnly)
        {
            var files = new StringCollection();
            files.AddRange(items.Where(i => !i.Missing).Select(i => i.Path).ToArray());
            if (files.Count > 0) data.SetFileDropList(files);
        }
        try { Clipboard.SetDataObject(data, copy: true); } catch { }
    }

    /// <summary>
    /// Moves or copies the selection to a folder the user picks. The shell does the
    /// work, then the list is caught up with where the files went.
    /// </summary>
    private async void TransferSelected(bool move)
    {
        List<FavItem> items = Selection().Where(i => !i.Missing).ToList();
        if (items.Count == 0) return;

        var pick = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"{(move ? "Move" : "Copy")} {Count(items.Count, "item")} to",
        };
        if (_lastFolder is not null) pick.InitialDirectory = _lastFolder;
        if (pick.ShowDialog(this) != true) return;
        string folder = pick.FolderName;
        _lastFolder = folder;

        // The heart lives in an NTFS stream, which most USB sticks and SD cards can't
        // hold. A copy losing it is fine — the original keeps its heart — but a move
        // takes the file out of the favourites for good.
        bool keepsHearts = AdsTag.VolumeSupportsStreams(folder);
        if (move && !keepsHearts)
        {
            MessageBoxResult answer = System.Windows.MessageBox.Show(this,
                "That drive can't store hearts — it isn't NTFS, and most USB sticks and SD cards aren't.\n\n" +
                "The files will be moved without their hearts and will drop out of your favourites. Move them anyway?",
                "Fav Explorer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        List<string> files = items
            .Where(i => !string.Equals(i.Folder.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Path)
            .ToList();
        if (files.Count == 0)
        {
            Notice(items.Count == 1 ? "It's already in that folder" : "They're already in that folder");
            return;
        }

        IntPtr owner = new WindowInteropHelper(this).Handle;
        List<Transfer> done = await ShellActions.TransferAsync(owner, files, folder, move);
        await Task.Run(() => _store.Transferred(done));

        int landed = done.Count(t => move
            ? !File.Exists(t.From) && !Directory.Exists(t.From)
            : File.Exists(t.To) || Directory.Exists(t.To));
        if (landed == 0) return; // cancelled, or every file skipped

        string where = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder;
        Notice($"{(move ? "Moved" : "Copied")} {Count(landed, "item")} to {where}" +
               (!move && !keepsHearts ? " — that drive can't keep hearts, so the copies have none" : ""));
    }

    private void ShareSelected()
    {
        List<FavItem> items = Selection().Where(i => !i.Missing).ToList();
        if (items.Count == 0) return;

        // The share panel is built on one folder's worth of items, in Explorer too.
        if (items.Select(i => i.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            Notice("Windows can only share files from one folder at a time");
            return;
        }
        if (!ShellActions.Share(new WindowInteropHelper(this).Handle, items.Select(i => i.Path).ToList()))
            Notice("Windows couldn't open the share panel for these");
    }

    private void RemoveSelected()
    {
        foreach (FavItem i in Selection()) _store.Remove(i.Path);
    }

    private void RefreshAll()
    {
        DropThumbnails();
        Reload();
    }

    private async void DropMissing()
    {
        int dropped = await Task.Run(_store.Reconcile);
        Notice(dropped == 0
            ? "Everything still checks out"
            : $"Dropped {dropped} entr{(dropped == 1 ? "y" : "ies")} whose file or tag was gone");
    }

    private void OnOpen(object sender, RoutedEventArgs e) => OpenItems(Selection());
    private void OnReveal(object sender, RoutedEventArgs e) => RevealSelected();
    private void OnCopy(object sender, RoutedEventArgs e) => CopySelected(pathsOnly: false);
    private void OnRemove(object sender, RoutedEventArgs e) => RemoveSelected();
    private void OnMoveTo(object sender, RoutedEventArgs e) => TransferSelected(move: true);
    private void OnCopyTo(object sender, RoutedEventArgs e) => TransferSelected(move: false);
    private void OnShare(object sender, RoutedEventArgs e) => ShareSelected();
    private void OnDetailsButton(object sender, RoutedEventArgs e) => SetView(FavView.Details);
    private void OnIconsButton(object sender, RoutedEventArgs e) => SetView(FavView.Large);

    // The Details columns in order, with what each one sorts by.
    private static readonly (string Label, FavSort Sort)[] Columns =
    {
        ("Name", FavSort.Name), ("Folder", FavSort.Folder), ("Favourited", FavSort.Added), ("Size", FavSort.Size),
    };

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header) return;
        int i = ((GridView)Resources["DetailsView"]).Columns.IndexOf(header.Column);
        if (i < 0 || i >= Columns.Length) return;
        FavSort s = Columns[i].Sort;
        SetSort(s, s == _sort ? !_descending : StartsDescending(s));
    }

    /// <summary>
    /// Marks the sorted column with a chevron after its name, as Explorer does,
    /// pointing up for ascending and down for descending.
    /// </summary>
    private void ShowSortArrow()
    {
        var columns = ((GridView)Resources["DetailsView"]).Columns;
        for (int i = 0; i < Columns.Length && i < columns.Count; i++)
        {
            if (Columns[i].Sort != _sort)
            {
                columns[i].Header = Columns[i].Label;
                continue;
            }

            var arrow = new TextBlock
            {
                Text = _descending ? "" : "",
                FontFamily = (System.Windows.Media.FontFamily)Resources["Glyphs"],
                FontSize = 10,
                Margin = new Thickness(8, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = Columns[i].Label, FontWeight = FontWeights.SemiBold });
            content.Children.Add(arrow);
            columns[i].Header = content;
        }
    }

    // --------------------------------------------------------------- menus --

    private void OnSortMenu(object sender, RoutedEventArgs e) => ShowMenu((Button)sender,
        Choice("Date favourited", _sort == FavSort.Added, () => SetSort(FavSort.Added, StartsDescending(FavSort.Added))),
        Choice("Name", _sort == FavSort.Name, () => SetSort(FavSort.Name, StartsDescending(FavSort.Name))),
        Choice("Folder", _sort == FavSort.Folder, () => SetSort(FavSort.Folder, StartsDescending(FavSort.Folder))),
        Choice("Size", _sort == FavSort.Size, () => SetSort(FavSort.Size, StartsDescending(FavSort.Size))),
        new Separator(),
        Choice("Ascending", !_descending, () => SetSort(_sort, false)),
        Choice("Descending", _descending, () => SetSort(_sort, true)),
        new Separator(),
        Submenu("Group by",
            Choice("Date favourited", _group == FavGroup.Date, () => SetGroup(FavGroup.Date)),
            Choice("Folder", _group == FavGroup.Folder, () => SetGroup(FavGroup.Folder)),
            Choice("(None)", _group == FavGroup.None, () => SetGroup(FavGroup.None))));

    // The same Ctrl+Shift+number shortcuts as Explorer's View menu.
    private void OnViewMenu(object sender, RoutedEventArgs e) => ShowMenu((Button)sender,
        Choice("Extra large icons", _mode == FavView.ExtraLarge, () => SetView(FavView.ExtraLarge), "Ctrl+Shift+1"),
        Choice("Large icons", _mode == FavView.Large, () => SetView(FavView.Large), "Ctrl+Shift+2"),
        Choice("Medium icons", _mode == FavView.Medium, () => SetView(FavView.Medium), "Ctrl+Shift+3"),
        Choice("Details", _mode == FavView.Details, () => SetView(FavView.Details), "Ctrl+Shift+6"),
        Choice("Tiles", _mode == FavView.Tiles, () => SetView(FavView.Tiles), "Ctrl+Shift+8"));

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        var items = new List<object>();

        // When the window is too narrow for them, these buttons are in here instead.
        if (_barLevel >= 2)
        {
            bool any = Favs.SelectedItems.Count > 0;
            foreach (MenuItem m in new[]
            {
                Choice("Move to…", false, () => TransferSelected(move: true)),
                Choice("Copy to…", false, () => TransferSelected(move: false)),
                Choice("Copy", false, () => CopySelected(pathsOnly: false), "Ctrl+C"),
                Choice("Share", false, ShareSelected),
            })
            {
                m.IsEnabled = any;
                items.Add(m);
            }
            items.Add(new Separator());
        }

        items.Add(Choice("Refresh", false, RefreshAll, "F5"));
        items.Add(Choice("Drop missing files", false, DropMissing));
        ShowMenu((Button)sender, items.ToArray());
    }

    private ContextMenu BuildItemMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Choice("Open", false, () => OpenItems(Selection()), "Enter"));
        menu.Items.Add(Choice("Show in folder", false, RevealSelected));
        menu.Items.Add(new Separator());
        menu.Items.Add(Choice("Copy", false, () => CopySelected(pathsOnly: false), "Ctrl+C"));
        menu.Items.Add(Choice("Copy as path", false, () => CopySelected(pathsOnly: true), "Ctrl+Shift+C"));
        menu.Items.Add(Choice("Share", false, ShareSelected));
        menu.Items.Add(new Separator());
        menu.Items.Add(Choice("Move to…", false, () => TransferSelected(move: true)));
        menu.Items.Add(Choice("Copy to…", false, () => TransferSelected(move: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Choice("Remove heart", false, RemoveSelected, "Delete"));
        return menu;
    }

    private static void ShowMenu(Button anchor, params object[] items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (object i in items) menu.Items.Add(i);
        menu.IsOpen = true;
    }

    private static MenuItem Choice(string text, bool isChecked, Action act, string? keys = null)
    {
        var m = new MenuItem { Header = text, IsChecked = isChecked, InputGestureText = keys ?? "" };
        m.Click += (_, _) => act();
        return m;
    }

    private static MenuItem Submenu(string text, params MenuItem[] items)
    {
        var m = new MenuItem { Header = text };
        foreach (MenuItem i in items) m.Items.Add(i);
        return m;
    }

    // ------------------------------------------------------- mouse and keys --

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        _collapseTo = null;
        if (ItemsControl.ContainerFromElement(Favs, (DependencyObject)e.OriginalSource) is not ListViewItem box
            || box.DataContext is not FavItem item) return;

        if (e.ClickCount == 2)
        {
            OpenItems(new[] { item });
            e.Handled = true;
            return;
        }

        _dragFrom = e.GetPosition(Favs);

        // A plain click on one of several selected items would collapse the selection
        // to that item before a drag could carry them all. Explorer waits for the
        // button to come up without a drag, and so does this.
        if (box.IsSelected && Favs.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _collapseTo = item;
            box.Focus();
            e.Handled = true;
        }
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not Point from || e.LeftButton != MouseButtonState.Pressed) return;
        Vector moved = e.GetPosition(Favs) - from;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragFrom = null;
        _collapseTo = null;
        string[] files = Selection().Where(i => !i.Missing).Select(i => i.Path).ToArray();
        if (files.Length == 0) return;

        // A plain file list, the same as a drag out of Explorer, so it drops into a
        // browser's upload box, an email or another folder alike.
        DragDrop.DoDragDrop(Favs, new DataObject(DataFormats.FileDrop, files),
            DragDropEffects.Copy | DragDropEffects.Link);
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_collapseTo is FavItem item)
        {
            Favs.SelectedItems.Clear();
            Favs.SelectedItem = item;
        }
        _collapseTo = null;
        _dragFrom = null;
    }

    private void OnListWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        int next = (int)_mode + (e.Delta > 0 ? 1 : -1);
        if (Enum.IsDefined(typeof(FavView), next)) SetView((FavView)next);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ModifierKeys mods = Keyboard.Modifiers;
        bool inSearch = e.OriginalSource is TextBox;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            FavView? view = key switch
            {
                Key.D1 => FavView.ExtraLarge,
                Key.D2 => FavView.Large,
                Key.D3 => FavView.Medium,
                Key.D6 => FavView.Details,
                Key.D8 => FavView.Tiles,
                _ => null,
            };
            if (view is FavView v) { SetView(v); e.Handled = true; }
            else if (key == Key.C && !inSearch) { CopySelected(pathsOnly: true); e.Handled = true; }
        }
        else if (key == Key.F5)
        {
            RefreshAll();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && key is Key.F or Key.E)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (inSearch)
        {
            if (key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; }
            else if (key is Key.Down or Key.Enter && _view.Count > 0)
            {
                Favs.SelectedItem ??= _view.GetItemAt(0);
                (Favs.ItemContainerGenerator.ContainerFromItem(Favs.SelectedItem) as ListViewItem)?.Focus();
                e.Handled = true;
            }
        }
        else if (mods == ModifierKeys.None && key == Key.Enter) { OpenItems(Selection()); e.Handled = true; }
        else if (mods == ModifierKeys.None && key == Key.Delete) { RemoveSelected(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && key == Key.C) { CopySelected(pathsOnly: false); e.Handled = true; }

        if (!e.Handled) base.OnPreviewKeyDown(e);
    }
}
