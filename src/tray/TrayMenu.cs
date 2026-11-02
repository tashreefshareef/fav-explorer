using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
// WinForms and System.Drawing are global usings for the rest of the app. In this
// file the shared names mean the WPF types.
using ContextMenu = System.Windows.Controls.ContextMenu;
using FontFamily = System.Windows.Media.FontFamily;
using MenuItem = System.Windows.Controls.MenuItem;

namespace FavExplorer;

/// <summary>
/// The tray icon's right-click menu, drawn by WPF so it matches the Fluent windows
/// rather than looking like a WinForms menu from another decade.
/// </summary>
internal sealed class TrayMenu
{
    private static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly ContextMenu _menu = new() { Placement = PlacementMode.MousePoint };

    public MenuItem Status { get; }
    public MenuItem Toggle { get; }
    public MenuItem Pause { get; }

    public TrayMenu(Action toggle, Action showAll, Action rescan, Action<bool> pause, Action settings, Action quit)
    {
        Status = new MenuItem { Header = "Fav Explorer", IsEnabled = false };
        Toggle = Item("Favourite selected files", "", toggle);
        Pause = Item("Pause the shortcut", "", () => pause(Pause!.IsChecked));
        Pause.IsCheckable = true;

        _menu.Items.Add(Status);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Toggle);
        _menu.Items.Add(Item("Show all favourites…", "", showAll));
        _menu.Items.Add(Item("Rescan a folder…", "", rescan));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Pause);
        _menu.Items.Add(Item("Settings…", "", settings));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Quit Fav Explorer", "", quit));
    }

    public void Show()
    {
        _menu.IsOpen = true;

        // A tray menu only goes away on a click elsewhere if its own window is the
        // foreground one. Explorer lets whoever owns the clicked icon take the
        // foreground for exactly this, so take it.
        if (PresentationSource.FromVisual(_menu) is HwndSource src)
            Native.SetForegroundWindow(src.Handle);
    }

    private static MenuItem Item(string text, string glyph, Action act)
    {
        var m = new MenuItem
        {
            Header = text,
            Icon = new TextBlock { Text = glyph, FontFamily = Glyphs, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
        };
        m.Click += (_, _) => act();
        return m;
    }
}
