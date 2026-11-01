using System.Text;

namespace FavExplorer;

/// <summary>
/// A deliberately tiny log, for the class of bug that leaves no other trace.
///
/// The index going stale is invisible from the outside: the handler correctly
/// rejects every file in a folder it does not know about, which looks exactly like
/// "nothing here is a favourite". Without a record of the write failing there is
/// nothing to investigate after the fact.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(Paths.Dir, "favexplorer.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Paths.Ensure();
                string path = Path;

                // Keep it bounded. A log that fills the disk is a worse bug than the
                // one it was added to catch.
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string[] lines = File.ReadAllLines(path);
                        File.WriteAllLines(path, lines.Skip(lines.Length / 2), new UTF8Encoding(false));
                    }
                }
                catch
                {
                }

                File.AppendAllText(path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never be the thing that breaks the app.
        }
    }
}
