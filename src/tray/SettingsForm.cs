using System.Windows.Forms;

namespace FavExplorer;

internal sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly FavoriteStore _store;
    private readonly Action<Settings> _onApplied;

    private readonly TextBox _hotkeyBox;
    private readonly CheckBox _toastBox;
    private readonly CheckBox _startupBox;
    private readonly Label _slotHeadline;
    private readonly Label _slotDetail;
    private readonly Label _indexHealth;
    private readonly ListBox _handlers;

    private Hotkey _pending;

    // A single consistent margin/width, so every control — headers, captions,
    // inputs, the list — lines up on the same left and right edge. The old
    // layout mixed 18s and 20s and 470s and 480s; nothing was actually
    // misaligned by eye, but nothing was deliberately aligned either.
    private const int Gutter = 24;
    private const int ContentWidth = 472;

    // Space after a header before its first control, and after a caption
    // before whatever follows it. Kept apart from SectionGap below so a
    // header always sits close to the content it introduces, while the
    // gap between one section's content and the next section's header
    // stays visibly larger — that's what makes the grouping readable at
    // a glance instead of everything reading as one undifferentiated list.
    private const int HeaderGap = 10;
    private const int CaptionGap = 12;
    private const int SectionGap = 26;

    public SettingsForm(Settings settings, FavoriteStore store, Action<Settings> onApplied)
    {
        _settings = settings;
        _store = store;
        _onApplied = onApplied;
        _pending = settings.Shortcut;

        Text = "Fav Explorer — Settings";
        Icon = AppIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(Gutter * 2 + ContentWidth, 640);
        Font = new Font("Segoe UI", 9f);

        int y = Gutter;

        Controls.Add(Header("Shortcut", ref y));
        Controls.Add(Caption("Click the box and press the combination you want. It only fires while File Explorer or the Photos viewer is in front.", 32, ref y));

        _hotkeyBox = new TextBox
        {
            Left = Gutter, Top = y, Width = 300, Height = 26, ReadOnly = true,
            Text = _pending.ToString(),
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Cascadia Mono", 10f, FontStyle.Regular),
        };
        _hotkeyBox.KeyDown += OnHotkeyCapture;
        _hotkeyBox.Enter += (_, _) => _hotkeyBox.Text = "press a combination…";
        _hotkeyBox.Leave += (_, _) => _hotkeyBox.Text = _pending.ToString();
        Controls.Add(_hotkeyBox);

        var reset = new Button { Left = Gutter + 316, Top = y - 1, Width = ContentWidth - 316, Height = 27, Text = "Default" };
        reset.Click += (_, _) => { _pending = Hotkey.Default; _hotkeyBox.Text = _pending.ToString(); };
        Controls.Add(reset);
        y += 26 + SectionGap;

        Controls.Add(Header("Behaviour", ref y));

        _toastBox = new CheckBox
        {
            Left = Gutter, Top = y, Width = ContentWidth, Height = 24,
            Text = "Show a confirmation after the shortcut fires",
            Checked = _settings.ShowToast,
        };
        Controls.Add(_toastBox);
        y += 28;

        _startupBox = new CheckBox
        {
            Left = Gutter, Top = y, Width = ContentWidth, Height = 24,
            Text = "Start Fav Explorer when I sign in",
            Checked = Settings.IsRegisteredAtStartup(),
        };
        Controls.Add(_startupBox);
        y += 30;

        Controls.Add(Caption("Hearts keep showing even when Fav Explorer is closed. What needs it running is changing them, and keeping the index below in step.", 32, ref y));
        y += SectionGap - CaptionGap;

        Controls.Add(Header("Is the heart actually being drawn?", ref y));

        _slotHeadline = new Label { Left = Gutter, Top = y, Width = ContentWidth, Height = 20, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        Controls.Add(_slotHeadline);
        y += 24;

        _slotDetail = new Label { Left = Gutter, Top = y, Width = ContentWidth, Height = 52, ForeColor = SystemColors.GrayText };
        Controls.Add(_slotDetail);
        y += 52 + 14;

        _indexHealth = new Label
        {
            Left = Gutter, Top = y, Width = ContentWidth - 100, Height = 36,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };
        Controls.Add(_indexHealth);

        var repair = new Button { Left = Gutter + ContentWidth - 90, Top = y, Width = 90, Height = 27, Text = "Repair" };
        repair.Click += (_, _) => { _store.CheckIndex(repair: true); RefreshDiagnostics(); };
        Controls.Add(repair);
        y += 36 + SectionGap - CaptionGap;

        Controls.Add(Caption("Windows keeps one global list of icon overlay handlers and only honours the first few. Handlers Fav Explorer is competing with:", 32, ref y));

        _handlers = new ListBox
        {
            Left = Gutter, Top = y, Width = ContentWidth, Height = 110,
            IntegralHeight = false,
            Font = new Font("Cascadia Mono", 8.5f),
        };
        _handlers.DrawMode = DrawMode.OwnerDrawFixed;
        _handlers.DrawItem += OnDrawHandler;
        Controls.Add(_handlers);
        y += 110 + SectionGap;

        var ok = new Button { Text = "Save", Left = Gutter + ContentWidth - 176, Top = y, Width = 84, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = Gutter + ContentWidth - 84, Top = y, Width = 84, Height = 28, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Apply();
        cancel.Click += (_, _) => Hide();
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        ClientSize = new Size(Gutter * 2 + ContentWidth, y + 28 + Gutter);

        RefreshDiagnostics();
    }

    private static Label Header(string text, ref int y)
    {
        var l = new Label
        {
            Left = Gutter, Top = y, Width = ContentWidth, Height = 24,
            Text = text,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
        };
        y += 24 + HeaderGap;
        return l;
    }

    private static Label Caption(string text, int height, ref int y)
    {
        var l = new Label
        {
            Left = Gutter, Top = y, Width = ContentWidth, Height = height,
            Text = text,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = SystemColors.GrayText,
        };
        y += height + CaptionGap;
        return l;
    }

    private void OnHotkeyCapture(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        Keys key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return; // still holding modifiers down

        var candidate = new Hotkey(e.Control, e.Shift, e.Alt, key);

        if (!e.Control && !e.Shift && !e.Alt)
        {
            MessageBox.Show(this,
                "The shortcut needs at least one of Ctrl, Shift or Alt.\n\n" +
                "A bare letter would break type-to-select in Explorer, where typing 'f' jumps to the first file starting with F.",
                "Fav Explorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (candidate.IsDangerous)
        {
            MessageBox.Show(this,
                $"{candidate} already means something destructive or surprising in File Explorer — " +
                "Ctrl+D deletes the selection, for instance.\n\nPick a combination with Shift or Alt in it.",
                "Fav Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _pending = candidate;
        _hotkeyBox.Text = _pending.ToString();
    }
    private void RefreshDiagnostics()
    {
        OverlayStatus s = OverlayDiagnostics.Read();

        _slotHeadline.Text = s.Headline;
        _slotHeadline.ForeColor = s.HasSlot ? Color.FromArgb(30, 110, 70) : Color.FromArgb(150, 60, 20);

        _slotDetail.Text = !s.Registered
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

        if (ix.InSync)
        {
            _indexHealth.Text = $"Index healthy — {ix.Favourites:N0} favourite{(ix.Favourites == 1 ? "" : "s")} " +
                                $"across {ix.FoldersOnDisk:N0} folder{(ix.FoldersOnDisk == 1 ? "" : "s")}, " +
                                $"{ix.Watching:N0} watched for changes";
            _indexHealth.ForeColor = Color.FromArgb(30, 110, 70);
        }
        else if (ix.FoldersOnDisk < 0)
        {
            _indexHealth.Text = "Index unreadable — hearts will not be drawn. Use Repair.";
            _indexHealth.ForeColor = Color.FromArgb(170, 40, 40);
        }
        else
        {
            _indexHealth.Text = $"Index out of step — holds {ix.FoldersOnDisk:N0} folder(s), " +
                                $"expected {ix.FoldersExpected:N0}. Hearts may be missing. Use Repair.";
            _indexHealth.ForeColor = Color.FromArgb(170, 40, 40);
        }

        _handlers.Items.Clear();
        foreach (var h in OverlayDiagnostics.ListHandlers())
            _handlers.Items.Add(h);
    }

    private void OnDrawHandler(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var (name, ours, honoured) = ((string, bool, bool))_handlers.Items[e.Index];

        e.DrawBackground();
        Color fore = !honoured ? SystemColors.GrayText
                   : ours ? Color.FromArgb(180, 26, 60)
                   : e.ForeColor;

        using var brush = new SolidBrush(fore);
        using var font = new Font(_handlers.Font, ours ? FontStyle.Bold : FontStyle.Regular);

        string label = $"{e.Index + 1,2}. {name}{(ours ? "   ← Fav Explorer" : "")}{(honoured ? "" : "   (ignored)")}";
        e.Graphics.DrawString(label, font, brush, e.Bounds.Left + 2, e.Bounds.Top + 1);
    }

    private void Apply()
    {
        _settings.Shortcut = _pending;
        _settings.ShowToast = _toastBox.Checked;
        _settings.RunAtStartup = _startupBox.Checked;
        _settings.Save();

        try { Settings.SetStartup(_startupBox.Checked); } catch { }

        _onApplied(_settings);
        Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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
        if (Visible)
        {
            _pending = _settings.Shortcut;
            _hotkeyBox.Text = _pending.ToString();
            _toastBox.Checked = _settings.ShowToast;
            _startupBox.Checked = Settings.IsRegisteredAtStartup();
            RefreshDiagnostics();
        }
    }
}
