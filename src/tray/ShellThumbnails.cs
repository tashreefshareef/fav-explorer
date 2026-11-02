using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FavExplorer;

/// <summary>
/// Thumbnails from the shell, the same ones Explorer draws, so a photo looks here
/// exactly as it does in its folder and usually comes straight out of the thumbnail
/// cache Explorer already filled.
///
/// Every request replaces whatever was still waiting, and the queue is served most
/// urgent first: what is on screen now matters more than what scrolled past.
/// </summary>
internal sealed class ShellThumbnails : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Action<FavItem, BitmapSource?, int> _done;
    private readonly object _gate = new();
    // Most urgent last, so taking one is a pop off the end.
    private readonly List<(FavItem Item, int Px)> _waiting = new();
    private bool _stopped;

    public ShellThumbnails(Dispatcher ui, Action<FavItem, BitmapSource?, int> done, int workers = 2)
    {
        _ui = ui;
        _done = done;

        for (int i = 0; i < workers; i++)
        {
            // Some thumbnail providers are apartment-threaded and misbehave on MTA.
            var t = new Thread(Work) { IsBackground = true, Name = "thumbnails" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    /// <summary>Replaces the queue. <paramref name="items"/> is most urgent first.</summary>
    public void Request(IReadOnlyList<FavItem> items, int px)
    {
        lock (_gate)
        {
            _waiting.Clear();
            for (int i = items.Count - 1; i >= 0; i--) _waiting.Add((items[i], px));
            Monitor.PulseAll(_gate);
        }
    }

    private void Work()
    {
        while (true)
        {
            FavItem item;
            int px;
            lock (_gate)
            {
                while (_waiting.Count == 0 && !_stopped) Monitor.Wait(_gate);
                if (_stopped) return;
                (item, px) = _waiting[^1];
                _waiting.RemoveAt(_waiting.Count - 1);
            }

            if (item.ThumbPixels >= px) continue;
            BitmapSource? bmp = Extract(item.Path, px);
            _ui.BeginInvoke(DispatcherPriority.Background, () => _done(item, bmp, px));
        }
    }

    private static BitmapSource? Extract(string path, int px)
    {
        Native.IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            Guid iid = typeof(Native.IShellItemImageFactory).GUID;
            if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory is null)
                return null;
            if (factory.GetImage(new Native.SIZE { cx = px, cy = px }, Native.SIIGBF_RESIZETOFIT, out hbm) != 0)
                return null;
            return hbm == IntPtr.Zero ? null : ToBitmapSource(hbm);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? ToBitmapSource(IntPtr hbm)
    {
        if (Native.GetObjectW(hbm, Marshal.SizeOf<Native.BITMAP>(), out Native.BITMAP bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;

        // Asking for a negative height gets the rows top-down whichever way the
        // shell stored them, which is what WPF expects.
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
        };
        byte[] pixels = new byte[w * h * 4];

        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(dc, hbm, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, dc);
        }

        // Photo thumbnails come back with the alpha channel all zero, which would
        // draw as nothing at all. Only icons carry real transparency.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }

        var bmp = BitmapSource.Create(w, h, 96, 96,
            hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            Monitor.PulseAll(_gate);
        }
    }
}
