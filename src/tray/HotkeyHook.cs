using System.Text;
using System.Windows.Forms;

namespace FavExplorer;

/// <summary>
/// A low-level keyboard hook, rather than RegisterHotKey, for one reason: this way
/// the shortcut is only swallowed while a File Explorer window or the Photos viewer
/// is in front. Every other app keeps Ctrl+Shift+F for itself.
///
/// The callback runs on the UI thread and is on a deadline — Windows drops hooks
/// that take longer than LowLevelHooksTimeout (300 ms by default), and a slow hook
/// makes every keystroke on the machine feel sticky. So this does the absolute
/// minimum and hands the real work off asynchronously.
/// </summary>
internal sealed class HotkeyHook : IDisposable
{
    private readonly Control _marshal;
    private readonly Action _onFired;

    // The delegate must be held in a field. If it is only passed to
    // SetWindowsHookEx, the GC will collect it and the process dies inside a
    // callback from the kernel, usually days later and never reproducibly.
    private readonly Native.HookProc _proc;

    private IntPtr _hook = IntPtr.Zero;
    private Hotkey _combo;

    public bool Paused { get; set; }

    public HotkeyHook(Control marshal, Hotkey combo, Action onFired)
    {
        _marshal = marshal;
        _combo = combo;
        _onFired = onFired;
        _proc = HookCallback;
    }

    public void Rebind(Hotkey combo) => _combo = combo;

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        // A WH_KEYBOARD_LL hook is global and needs no module handle of its own.
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || Paused) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        if (msg is not (Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN))
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = System.Runtime.InteropServices.Marshal
            .PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);

        if ((Keys)info.vkCode != _combo.Key ||
            Native.IsDown(Native.VK_CONTROL) != _combo.Ctrl ||
            Native.IsDown(Native.VK_SHIFT)   != _combo.Shift ||
            Native.IsDown(Native.VK_MENU)    != _combo.Alt ||
            Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))
        {
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (!ExplorerSelection.IsExplorerForeground() && !PhotosViewer.IsForeground())
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        // Post and return. Reading the selection means COM calls and touching the
        // disk; doing that here would blow the hook deadline.
        try { _marshal.BeginInvoke(_onFired); } catch { }

        return 1; // swallowed, so Explorer never sees it
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
