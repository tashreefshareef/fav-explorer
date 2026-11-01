using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FavExplorer;

/// <summary>
/// The little confirmation pill that appears after the shortcut fires.
///
/// It exists because the shortcut is otherwise invisible: if the heart lands on a
/// thumbnail that happens to be scrolled off screen, nothing tells you it worked.
/// A balloon tip would do the job but takes a second to appear and pulls attention
/// to the notification area, which is the wrong place to look.
///
/// The one hard requirement is that it must never take focus. Stealing focus from
/// Explorer would break the selection the user is working through.
/// </summary>
internal sealed class Toast : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST    = 0x00000008;

    private static Toast? _current;

    private readonly string _text;
    private readonly System.Windows.Forms.Timer _life = new();

    private Toast(string text)
    {
        _text = text;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 20, 22);

        using (var probe = CreateGraphics())
        {
            SizeF size = probe.MeasureString(_text, TextFont);
            Width = (int)size.Width + 74;
            Height = 46;
        }

        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 64);

        Region = new Region(RoundedPath(new Rectangle(0, 0, Width, Height), Height / 2));

        _life.Interval = 1900;
        _life.Tick += (_, _) => Close();
    }

    private static readonly Font TextFont = new("Segoe UI", 10.5f, FontStyle.Regular);

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    // Belt and braces alongside WS_EX_NOACTIVATE: never become the active window.
    protected override bool ShowWithoutActivation => true;

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        DrawHeart(g, new RectangleF(20, Height / 2f - 8, 17, 16),
                  Color.FromArgb(255, 92, 122));

        using var text = new SolidBrush(Color.FromArgb(242, 236, 238));
        using var fmt = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString(_text, TextFont, text, new RectangleF(48, 0, Width - 60, Height), fmt);
    }

    private static readonly PointF[] HeartUnit =
    {
        new(0.50f, 0.27f),
        new(0.61f, 0.08f), new(0.97f, 0.11f), new(0.96f, 0.38f),
        new(0.95f, 0.63f), new(0.63f, 0.79f), new(0.50f, 0.96f),
        new(0.37f, 0.79f), new(0.05f, 0.63f), new(0.04f, 0.38f),
        new(0.03f, 0.11f), new(0.39f, 0.08f), new(0.50f, 0.27f),
    };

    internal static void DrawHeart(Graphics g, RectangleF box, Color color)
    {
        var pts = new PointF[HeartUnit.Length];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new PointF(box.X + HeartUnit[i].X * box.Width,
                                box.Y + HeartUnit[i].Y * box.Height);

        using var path = new GraphicsPath();
        path.AddBeziers(pts);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _life.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _life.Stop();
        _life.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnFormClosed(e);
    }

    public static void Show(string text)
    {
        try
        {
            _current?.Close();
            _current = new Toast(text);
            _current.Show();
        }
        catch
        {
            // Feedback is a nicety. It must never take the toggle down with it.
        }
    }
}
