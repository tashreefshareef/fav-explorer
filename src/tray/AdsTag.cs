using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FavExplorer;

/// <summary>
/// The tag itself: an NTFS alternate data stream called <c>favexplorer</c> hanging off
/// the file. Presence of the stream is the flag; the bytes inside it are only there
/// so the favourites list can still sort by date if its cache is ever lost.
///
/// This is the single source of truth. Everything else in the app — the index, the
/// favourites list — is a cache that can be rebuilt from these streams.
/// </summary>
internal static class AdsTag
{
    public const string StreamSuffix = ":favexplorer";

    private static string StreamPath(string path) => path + StreamSuffix;

    public static bool IsTagged(string path)
    {
        try
        {
            using SafeFileHandle h = Native.CreateFileW(
                StreamPath(path), Native.FILE_READ_ATTRIBUTES,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            return !h.IsInvalid;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------- leaving no trace --

    /// <summary>
    /// A file's timestamps and attributes, captured so they can be put back.
    ///
    /// NTFS keeps timestamps in the file's $STANDARD_INFORMATION record, which every
    /// stream on that file shares. There is no way to write an alternate data stream
    /// without restamping the file itself and setting its archive bit — which reorders
    /// folders sorted by Date modified, makes that column report when you hearted a
    /// photo rather than when you took it, and, worst of all, makes every sync client
    /// on the machine believe the file changed and re-upload it.
    ///
    /// Hearting a file is not a modification to it. So we put the stamps back.
    /// </summary>
    private readonly record struct FileStamps(
        long Creation, long LastAccess, long LastWrite, FileAttributes Attributes, bool HasAttributes);

    private static FileStamps? CaptureStamps(string path)
    {
        try
        {
            using SafeFileHandle h = Native.CreateFileW(
                path, Native.FILE_READ_ATTRIBUTES,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) return null;
            if (!Native.GetFileTime(h, out long c, out long a, out long w)) return null;

            FileAttributes attrs = default;
            bool gotAttrs = false;
            try { attrs = File.GetAttributes(path); gotAttrs = true; } catch { }

            return new FileStamps(c, a, w, attrs, gotAttrs);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the captured stamps back. Must run only after the stream handle is closed —
    /// closing a handle that has written data restamps the file all over again.
    /// </summary>
    private static void RestoreStamps(string path, FileStamps? saved)
    {
        if (saved is not FileStamps s) return;

        try
        {
            using (SafeFileHandle h = Native.CreateFileW(
                path, Native.FILE_WRITE_ATTRIBUTES,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (!h.IsInvalid)
                {
                    long c = s.Creation, a = s.LastAccess, w = s.LastWrite;
                    Native.SetFileTime(h, ref c, ref a, ref w);
                }
            }

            // The archive bit is how backup software decides what to re-copy, so it
            // matters for the same reason the timestamps do.
            if (s.HasAttributes)
            {
                try { File.SetAttributes(path, s.Attributes); } catch { }
            }
        }
        catch
        {
            // Losing the restore is cosmetic — the tag itself is fine, and reporting
            // the tag as failed would be a worse lie than a bumped timestamp.
        }
    }

    // -------------------------------------------------------------- tag/untag --

    public static bool Tag(string path)
    {
        FileStamps? stamps = CaptureStamps(path);
        bool written = false;

        try
        {
            using SafeFileHandle h = Native.CreateFileW(
                StreamPath(path), Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ, IntPtr.Zero,
                Native.CREATE_ALWAYS, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid)
            {
                Log.Write($"tag failed for {path}: win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                return false;
            }

            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            byte[] payload = Encoding.UTF8.GetBytes($"FE1 {ms}\n");

            // Scoped so the stream handle is closed before the restore in the finally.
            using (var fs = new FileStream(h, FileAccess.Write))
            {
                fs.Write(payload, 0, payload.Length);
            }
            written = true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (written) RestoreStamps(path, stamps);
        }

        return true;
    }

    public static bool Untag(string path)
    {
        FileStamps? stamps = CaptureStamps(path);

        try
        {
            // DeleteFile on a stream path removes just that stream, leaving the file
            // itself completely untouched.
            bool ok = Native.DeleteFileW(StreamPath(path));
            if (!ok) ok = !IsTagged(path);   // already gone counts as success
            RestoreStamps(path, stamps);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public static DateTimeOffset? ReadTaggedAt(string path)
    {
        try
        {
            using SafeFileHandle h = Native.CreateFileW(
                StreamPath(path), 0x80000000 /* GENERIC_READ */,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) return null;

            using var fs = new FileStream(h, FileAccess.Read);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line = sr.ReadLine();
            if (line is null) return null;

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // HM1 is what Heartmark wrote; MigrateLegacy copies those bytes across as-is.
            if (parts.Length >= 2 && parts[0] is "FE1" or "HM1" && long.TryParse(parts[1], out long ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ----------------------------------------------------------- old name --

    /// <summary>The stream this app wrote while it was called Heartmark.</summary>
    public const string LegacySuffix = ":heartmark";

    /// <summary>
    /// Moves a Heartmark tag onto the current stream name, keeping its contents (and
    /// so the date it was hearted) and the file's own timestamps. False when the file
    /// carries no legacy tag, which is the cheap and common answer.
    /// </summary>
    public static bool MigrateLegacy(string path)
    {
        byte[] payload;
        try
        {
            using SafeFileHandle h = Native.CreateFileW(
                path + LegacySuffix, 0x80000000 /* GENERIC_READ */,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) return false;

            using var fs = new FileStream(h, FileAccess.Read);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            payload = ms.ToArray();
        }
        catch
        {
            return false;
        }

        FileStamps? stamps = CaptureStamps(path);
        try
        {
            if (!IsTagged(path))
            {
                using SafeFileHandle h = Native.CreateFileW(
                    StreamPath(path), Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ, IntPtr.Zero,
                    Native.CREATE_ALWAYS, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (h.IsInvalid) return false;
                using var fs = new FileStream(h, FileAccess.Write);
                fs.Write(payload, 0, payload.Length);
            }

            // Only once the new tag is safely written, so a failure halfway leaves the
            // file still hearted under one name or the other.
            Native.DeleteFileW(path + LegacySuffix);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            RestoreStamps(path, stamps);
        }
    }

    /// <summary>
    /// Alternate data streams are an NTFS/ReFS feature. On a FAT32 stick or an exFAT
    /// card there is nowhere to put the tag, and silently doing nothing would be the
    /// worst possible behaviour — so the caller checks first and says so.
    /// </summary>
    public static bool VolumeSupportsStreams(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            if (!root.EndsWith('\\')) root += '\\';

            // A UNC share reports the server's filesystem, which is usually right, but
            // some servers lie. Attempting the tag and checking is more honest there.
            if (root.StartsWith(@"\\")) return true;

            var fsName = new StringBuilder(64);
            if (!Native.GetVolumeInformationW(root, IntPtr.Zero, 0, out _, out _, out _, fsName, fsName.Capacity))
                return false;

            string name = fsName.ToString();
            return name.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
