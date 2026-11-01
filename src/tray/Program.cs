using System.Windows.Forms;

namespace FavExplorer;

internal static class Program
{
    // Per-user rather than global: two people signed into the same machine each get
    // their own copy, tagging files with their own account's permissions.
    private const string InstanceMutex = @"Local\FavExplorer.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutex, out bool isFirst);
        if (!isFirst)
        {
            // A second copy would install a second keyboard hook and fight the first
            // one over the index file.
            MessageBox.Show(
                "Fav Explorer is already running — look for the heart in the notification area.",
                "Fav Explorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);

        LegacyMigration.Run();

        try
        {
            Application.Run(new TrayApp());
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void Fatal(Exception? ex)
    {
        try
        {
            Paths.Ensure();
            File.AppendAllText(Path.Combine(Paths.Dir, "crash.log"),
                $"{DateTimeOffset.Now:u}  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }

        MessageBox.Show(
            $"Fav Explorer hit an unexpected error and needs to close.\n\n{ex?.Message}\n\n" +
            $"Details were written to {Path.Combine(Paths.Dir, "crash.log")}.\n\n" +
            "Your hearts are stored on the files themselves and are unaffected.",
            "Fav Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
