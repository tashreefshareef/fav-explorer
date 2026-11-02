using System.Runtime.InteropServices;

namespace FavExplorer;

internal sealed record Transfer(string From, string To);

/// <summary>
/// Moving, copying and sharing files the way Explorer does it, by asking the shell
/// to do it, so the user gets the dialogs they already know: progress, "replace or
/// skip", Ctrl+Z to undo, and the Windows share panel.
/// </summary>
internal static class ShellActions
{
    /// <summary>
    /// Moves or copies <paramref name="files"/> into <paramref name="folder"/> and
    /// reports where each one was meant to land. Runs on its own thread so a long
    /// copy doesn't freeze the window that asked for it.
    /// </summary>
    public static Task<List<Transfer>> TransferAsync(
        IntPtr owner, IReadOnlyList<string> files, string folder, bool move)
    {
        var done = new TaskCompletionSource<List<Transfer>>();
        var t = new Thread(() =>
        {
            try
            {
                var op = new Native.SHFILEOPSTRUCT
                {
                    hwnd = owner,
                    wFunc = move ? Native.FO_MOVE : Native.FO_COPY,
                    // Both lists are NUL-separated and end with an extra NUL.
                    pFrom = string.Join('\0', files) + "\0\0",
                    pTo = folder + "\0\0",
                    fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR),
                };
                Native.SHFileOperationW(ref op);
                done.SetResult(files.Select(f => new Transfer(f, Path.Combine(folder, Path.GetFileName(f)))).ToList());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "file transfer" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    /// <summary>
    /// Opens the Windows share panel for these files, through the same Share verb
    /// Explorer's own Share button invokes. They must all be in one folder, as they
    /// must be in Explorer.
    /// </summary>
    public static bool Share(IntPtr owner, IReadOnlyList<string> files)
    {
        var pidls = new List<IntPtr>(files.Count);
        Native.IShellItemArray? items = null;
        object? handler = null;
        IntPtr menu = IntPtr.Zero;
        try
        {
            foreach (string f in files)
            {
                if (Native.SHParseDisplayName(f, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0) return false;
                pidls.Add(pidl);
            }
            if (Native.SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out items) != 0 || items is null)
                return false;

            Guid bhid = Native.BHID_SFUIObject;
            Guid iid = typeof(Native.IContextMenu).GUID;
            if (items.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out handler) != 0 || handler is not Native.IContextMenu cm)
                return false;

            // Some handlers only answer to a verb once they have been asked to fill a menu.
            menu = Native.CreatePopupMenu();
            cm.QueryContextMenu(menu, 0, 1, 0x7FFF, Native.CMF_NORMAL);

            var info = new Native.CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<Native.CMINVOKECOMMANDINFOEX>(),
                fMask = Native.CMIC_MASK_UNICODE,
                hwnd = owner,
                lpVerb = "Windows.ModernShare",
                lpVerbW = "Windows.ModernShare",
                nShow = 1,
            };
            return cm.InvokeCommand(ref info) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (menu != IntPtr.Zero) Native.DestroyMenu(menu);
            if (handler is not null) Marshal.ReleaseComObject(handler);
            if (items is not null) Marshal.ReleaseComObject(items);
            foreach (IntPtr p in pidls) Native.ILFree(p);
        }
    }
}
