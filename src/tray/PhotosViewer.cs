using System.Runtime.InteropServices;
using System.Text;

namespace FavExplorer;

/// <summary>
/// Works out which file the Windows Photos viewer is showing, so the shortcut can
/// tag it while the user is arrowing through a folder full-screen instead of
/// picking thumbnails in Explorer.
///
/// Photos gives nothing away through the shell, so this is pieced together from two
/// things that are stable OS-level facts rather than anything inside the app:
///
///   - Each viewer window is its own process, launched as
///     <c>Photos.exe ms-photos:viewer?fileName=C:\...\first.jpg</c> (or with a plain
///     path argument). That names the file the window was opened on, and never
///     changes afterwards.
///   - The window title is the bare file name of whatever is on screen right now,
///     and it does update as the user moves through the folder.
///
/// Next/Previous never leave the folder the viewer was opened in, so the launch
/// file's folder plus the current title is the current path. Windows hosted in a
/// spare process have no launch file; for those the folder comes from the open
/// Explorer windows instead (see <see cref="FindInOpenFolders"/>). Either way the
/// result is checked against the disk before anything is tagged; if nothing fits
/// the answer is "don't know", not a guess.
/// </summary>
internal static class PhotosViewer
{
    private const string WindowClass = "WinUIDesktopWin32WindowClass";
    private const string ExeName = "Photos.exe";

    /// <summary>
    /// Cheap enough for the keyboard hook: a class-name read, and only when that
    /// matches, one limited-rights process open to confirm it really is Photos.
    /// </summary>
    public static bool IsForeground()
    {
        IntPtr fg = Native.GetForegroundWindow();
        return fg != IntPtr.Zero && IsViewerWindow(fg);
    }

    /// <summary>The path of the photo on screen, or null if that can't be established.</summary>
    public static string? GetCurrentPath()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsViewerWindow(fg)) return null;

        string title = TitleOf(fg);
        if (title.Length == 0 || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        Native.GetWindowThreadProcessId(fg, out uint pid);
        string? launched = LaunchFileOf(pid);
        string? folder = launched is null ? null : Path.GetDirectoryName(launched);
        if (!string.IsNullOrEmpty(folder))
        {
            string candidate = Path.Combine(folder, title);
            if (File.Exists(candidate)) return candidate;
        }

        return FindInOpenFolders(fg, title);
    }

    /// <summary>
    /// Newer Photos builds keep idle "spare" processes
    /// (<c>Photos.exe ms-photos:spareprocess-viewer</c>) and hand the file to one after
    /// it has started, so most viewer windows have no file on their command line.
    ///
    /// Opening a photo nearly always means double-clicking it in an Explorer window
    /// that is still open behind the viewer, so look for the title in the folders
    /// Explorer is showing, then on the desktop. When the same name exists in more
    /// than one of those, the Explorer window closest under the viewer wins: that is
    /// the one the photo was just opened from.
    /// </summary>
    internal static string? FindInOpenFolders(IntPtr viewer, string title)
    {
        var zOrder = new Dictionary<IntPtr, int>();
        int rank = 0;
        for (IntPtr h = Native.GetWindow(viewer, Native.GW_HWNDNEXT);
             h != IntPtr.Zero && rank < 10_000;
             h = Native.GetWindow(h, Native.GW_HWNDNEXT))
            zOrder[h] = rank++;

        string? best = null;
        int bestRank = int.MaxValue;
        foreach (var (hwnd, folder) in ExplorerSelection.GetOpenFolders())
        {
            string candidate = Path.Combine(folder, title);
            int r = zOrder.TryGetValue(hwnd, out int z) ? z : int.MaxValue - 1;
            if (r < bestRank && File.Exists(candidate)) { best = candidate; bestRank = r; }
        }
        if (best is not null) return best;

        foreach (var special in new[] { Environment.SpecialFolder.DesktopDirectory,
                                        Environment.SpecialFolder.CommonDesktopDirectory })
        {
            string candidate = Path.Combine(Environment.GetFolderPath(special), title);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static bool IsViewerWindow(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        if (Native.GetClassNameW(hwnd, cls, cls.Capacity) <= 0 || cls.ToString() != WindowClass)
            return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        string? exe = ImagePathOf(pid);
        return exe is not null &&
               Path.GetFileName(exe).Equals(ExeName, StringComparison.OrdinalIgnoreCase);
    }

    internal static string TitleOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        return Native.GetWindowTextW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    internal static string? ImagePathOf(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            return Native.QueryFullProcessImageNameW(h, 0, sb, ref len) ? sb.ToString(0, len) : null;
        }
        finally { Native.CloseHandle(h); }
    }

    private static string? LaunchFileOf(uint pid)
    {
        string? cmd = CommandLineOf(pid);
        return cmd is null ? null : ParseLaunchFile(cmd);
    }

    /// <summary>
    /// Reads another process's command line the way Task Manager does. Needs only
    /// limited query rights, which one user's processes always have on each other.
    /// </summary>
    internal static string? CommandLineOf(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;

        IntPtr buf = IntPtr.Zero;
        try
        {
            int status = Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
            if (status != Native.STATUS_INFO_LENGTH_MISMATCH || needed <= 0) return null;

            buf = Marshal.AllocHGlobal(needed);
            status = Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, buf, needed, out _);
            if (status != 0) return null;

            var us = Marshal.PtrToStructure<Native.UNICODE_STRING>(buf);
            return us.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        catch { return null; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            Native.CloseHandle(h);
        }
    }

    /// <summary>
    /// Photos has been seen launched two ways: with a <c>ms-photos:viewer?fileName=</c>
    /// URI, and with the path as an ordinary argument. Neither is guaranteed to be
    /// quoted, and the URI form is passed with its spaces intact rather than escaped.
    /// </summary>
    internal static string? ParseLaunchFile(string commandLine)
    {
        const string key = "fileName=";
        int at = commandLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);

        string raw;
        if (at >= 0)
        {
            raw = commandLine[(at + key.Length)..].Trim().TrimEnd('"');
        }
        else
        {
            // Skip the executable, which is always the first (quoted) token.
            int afterExe = commandLine.StartsWith('"')
                ? commandLine.IndexOf('"', 1) + 1
                : commandLine.IndexOf(' ');
            if (afterExe <= 0 || afterExe >= commandLine.Length) return null;
            raw = commandLine[afterExe..].Trim().Trim('"');
        }

        if (raw.Length == 0 || !Path.IsPathRooted(raw)) return null;
        if (File.Exists(raw)) return raw;

        // Only unescape if the literal path isn't there — a real '%' in a file name
        // must not be mangled on the way through.
        if (raw.Contains('%'))
        {
            string unescaped = Uri.UnescapeDataString(raw);
            if (File.Exists(unescaped)) return unescaped;
        }
        return null;
    }
}
