using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FavExplorer;

/// <summary>
/// Moving, copying and sharing files the way Explorer does it, by asking Windows to
/// do it, so the user gets the dialogs they already know: progress, "replace or
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

    // ---------------------------------------------------------------- share --

    // One DataTransferManager per window, asked for its data each time the panel
    // opens. Explorer's own Share verb looked like a shortcut to this, but it only
    // works from inside Explorer: anywhere else it fails with "invalid window handle".
    private static readonly Dictionary<IntPtr, DataTransferManager> Managers = new();
    private static IReadOnlyList<string> _sharing = Array.Empty<string>();

    /// <summary>Opens the Windows share panel for these files, anchored to <paramref name="owner"/>.</summary>
    public static bool Share(IntPtr owner, IReadOnlyList<string> files)
    {
        try
        {
            if (!Managers.TryGetValue(owner, out DataTransferManager? manager))
            {
                manager = DataTransferManagerInterop.GetForWindow(owner);
                manager.DataRequested += OnDataRequested;
                Managers[owner] = manager;
            }
            _sharing = files.ToList();
            DataTransferManagerInterop.ShowShareUIForWindow(owner);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"share panel failed: {ex.Message}");
            return false;
        }
    }

    private static async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        DataRequest request = args.Request;
        DataRequestDeferral deferral = request.GetDeferral();
        try
        {
            IReadOnlyList<string> files = _sharing;
            var items = new List<IStorageItem>(files.Count);
            foreach (string f in files)
            {
                items.Add(Directory.Exists(f)
                    ? await StorageFolder.GetFolderFromPathAsync(f)
                    : await StorageFile.GetFileFromPathAsync(f));
            }

            request.Data.Properties.Title = files.Count == 1
                ? Path.GetFileName(files[0].TrimEnd('\\'))
                : $"{files.Count} favourites";
            request.Data.SetStorageItems(items);
        }
        catch (Exception ex)
        {
            Log.Write($"share data failed: {ex.Message}");
            request.FailWithDisplayText("Fav Explorer couldn't open those files to share them.");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
