using System.Runtime.InteropServices;
using System.Text;

namespace FavExplorer;

/// <summary>
/// Asks the Explorer window that currently has focus what the user has selected.
///
/// This goes through Shell.Application's IDispatch surface rather than hand-written
/// IShellWindows / IFolderView2 interop. It is the same object model, reached the
/// way a script would reach it, and it replaces several hundred lines of interface
/// declarations with about twenty lines that are much harder to get subtly wrong.
/// </summary>
internal static class ExplorerSelection
{
    // CabinetWClass is a normal Explorer window; ExploreWClass is the older
    // folder-tree variant. Progman and WorkerW are the desktop, which is a shell
    // view like any other and worth supporting — plenty of photos live there.
    private static readonly string[] ExplorerClasses =
        { "CabinetWClass", "ExploreWClass", "Progman", "WorkerW" };

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return Native.GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    public static bool IsExplorerForeground()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        string cls = ClassNameOf(fg);
        return Array.IndexOf(ExplorerClasses, cls) >= 0;
    }

    /// <summary>
    /// Returns the filesystem paths currently selected in the focused Explorer
    /// window, or an empty list if focus is somewhere else or the selection holds
    /// nothing that lives on disk.
    /// </summary>
    public static List<string> GetSelectedPaths()
    {
        var result = new List<string>();

        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return result;

        string cls = ClassNameOf(fg);
        if (Array.IndexOf(ExplorerClasses, cls) < 0) return result;

        // For the desktop, the window that owns the shell view is the one
        // GetShellWindow reports, not necessarily the foreground WorkerW.
        bool isDesktop = cls is "Progman" or "WorkerW";
        IntPtr target = isDesktop ? Native.GetShellWindow() : fg;

        object? shellObj = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return result;

            shellObj = Activator.CreateInstance(shellType);
            if (shellObj is null) return result;

            dynamic shell = shellObj;
            dynamic windows = shell.Windows();

            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? w = null;
                try
                {
                    w = windows.Item(i);
                    if (w is null) continue;

                    var hwnd = new IntPtr((long)w.HWND);
                    if (hwnd != target) continue;

                    dynamic doc = w.Document;
                    dynamic items = doc.SelectedItems();

                    int n = items.Count;
                    for (int j = 0; j < n; j++)
                    {
                        try
                        {
                            string? p = items.Item(j)?.Path as string;
                            // Virtual items — This PC, a search result, a library
                            // header — have no rooted path and cannot carry a stream.
                            if (!string.IsNullOrEmpty(p) && Path.IsPathRooted(p))
                                result.Add(p);
                        }
                        catch (COMException) { }
                    }
                    break;
                }
                catch (COMException)
                {
                    // A window can close between the enumeration and the query.
                }
                finally
                {
                    if (w is not null && Marshal.IsComObject(w)) Marshal.ReleaseComObject(w);
                }
            }
        }
        catch (COMException)
        {
        }
        catch (InvalidCastException)
        {
        }
        finally
        {
            if (shellObj is not null && Marshal.IsComObject(shellObj))
                Marshal.ReleaseComObject(shellObj);
        }

        return result;
    }

    /// <summary>
    /// Opens an Explorer window on the file's folder with the file itself selected.
    /// Used by the favourites list.
    /// </summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }
}
