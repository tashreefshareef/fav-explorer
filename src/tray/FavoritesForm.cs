using System.Windows.Forms;

namespace FavExplorer;

/// <summary>
/// Every favourite in one list, newest first, with a way back to the file.
///
/// This is the answer to "I hearted it three months ago, where is it now" — the
/// question the hearts on the thumbnails cannot answer on their own.
/// </summary>
internal sealed class FavoritesForm : Form
{
    private readonly FavoriteStore _store;
    private readonly ListView _list;
    private readonly Label _empty;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusText;

    public FavoritesForm(FavoriteStore store)
    {
        _store = store;

        Text = "Fav Explorer — Favourites";
        Icon = AppIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 380);
        Size = new Size(860, 560);
        Font = new Font("Segoe UI", 9f);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            OwnerDraw = false,
            BorderStyle = BorderStyle.None,
        };
        _list.Columns.Add("Name", 240);
        _list.Columns.Add("Folder", 400);
        _list.Columns.Add("Favourited", 150);

        _list.DoubleClick += (_, _) => RevealSelected();
        _list.KeyDown += OnListKeyDown;
        _list.ContextMenuStrip = BuildContextMenu();

        _empty = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            Text = "Nothing hearted yet.\n\nSelect photos in File Explorer and press the shortcut.",
            Visible = false,
        };

        var toolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
        };
        toolbar.Items.Add(new ToolStripButton("Open location", null, (_, _) => RevealSelected()));
        toolbar.Items.Add(new ToolStripButton("Remove heart", null, (_, _) => RemoveSelected()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripButton("Refresh", null, (_, _) => Reload()));
        toolbar.Items.Add(new ToolStripButton("Drop missing files", null, (_, _) => ReconcileNow()));

        _statusText = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _status = new StatusStrip();
        _status.Items.Add(_statusText);

        Controls.Add(_empty);
        Controls.Add(_list);
        Controls.Add(toolbar);
        Controls.Add(_status);

        Reload();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add(new ToolStripMenuItem("Open file location", null, (_, _) => RevealSelected()));
        m.Items.Add(new ToolStripMenuItem("Copy full path", null, (_, _) => CopyPaths()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Remove heart", null, (_, _) => RemoveSelected()));
        return m;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter) { RevealSelected(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.C) { CopyPaths(); e.Handled = true; }
    }

    private void Reload()
    {
        List<FavoriteEntry> items = _store.Snapshot();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (FavoriteEntry e in items)
        {
            string folder = Path.GetDirectoryName(e.Path) ?? "";
            var row = new ListViewItem(new[]
            {
                Path.GetFileName(e.Path.TrimEnd('\\')),
                folder,
                e.AddedUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm"),
            })
            { Tag = e.Path };

            // A file that has gone missing is worth showing rather than hiding —
            // otherwise it just silently disappears and you wonder if you imagined it.
            if (!File.Exists(e.Path) && !Directory.Exists(e.Path))
            {
                row.ForeColor = SystemColors.GrayText;
                row.SubItems[0].Text += "  (missing)";
            }
            _list.Items.Add(row);
        }
        _list.EndUpdate();

        bool any = items.Count > 0;
        _list.Visible = any;
        _empty.Visible = !any;
        _statusText.Text = any
            ? $"{items.Count:N0} favourite{(items.Count == 1 ? "" : "s")} across {_store.IndexedFolderCount:N0} folder{(_store.IndexedFolderCount == 1 ? "" : "s")}"
            : "No favourites yet";
    }

    private IEnumerable<string> SelectedPaths() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!);

    private void RevealSelected()
    {
        string? p = SelectedPaths().FirstOrDefault();
        if (p is not null) ExplorerSelection.RevealInExplorer(p);
    }

    private void CopyPaths()
    {
        List<string> paths = SelectedPaths().ToList();
        if (paths.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { }
    }

    private void RemoveSelected()
    {
        List<string> paths = SelectedPaths().ToList();
        if (paths.Count == 0) return;

        foreach (string p in paths) _store.Remove(p);
        Reload();
    }

    private void ReconcileNow()
    {
        int dropped = _store.Reconcile();
        Reload();
        _statusText.Text = dropped == 0
            ? "Everything still checks out"
            : $"Dropped {dropped} entr{(dropped == 1 ? "y" : "ies")} whose file or tag was gone";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Quitting is the tray icon's job. Closing this window just puts it away.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Reload();
    }
}
