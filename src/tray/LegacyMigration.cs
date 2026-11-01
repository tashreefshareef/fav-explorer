namespace FavExplorer;

/// <summary>
/// One-time move from Heartmark, this app's name before 0.1.0 was re-released as
/// Fav Explorer. Heartmark's hearts live on a differently named stream and its list
/// in a differently named folder, so without this every existing favourite would
/// silently vanish on upgrade.
///
/// Runs before the store loads, so the store sees the migrated list as its own. A
/// marker file in the old folder makes it run once; the old folder is otherwise left
/// alone, as Heartmark's uninstaller leaves it.
/// </summary>
internal static class LegacyMigration
{
    private static readonly string OldDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heartmark");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string OldRunValue = "Heartmark";

    public static void Run()
    {
        try
        {
            string oldList = Path.Combine(OldDir, "favorites.tsv");
            string marker = Path.Combine(OldDir, "moved-to-fav-explorer");
            if (!File.Exists(oldList) || File.Exists(marker)) return;

            Paths.Ensure();
            var kept = new List<string>();
            int lost = 0;

            foreach (string line in File.ReadLines(oldList))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string path = line.Split('\t')[0];

                // Already on the new stream counts too: a migration interrupted halfway
                // through is simply finished by the next run.
                if (AdsTag.MigrateLegacy(path) || AdsTag.IsTagged(path)) kept.Add(line);
                else lost++;
            }

            // Appending is safe even if Fav Explorer already has a list: the store keys
            // entries by path, so a duplicate line just restates the same favourite.
            File.AppendAllLines(Paths.Favorites, kept);

            string oldConfig = Path.Combine(OldDir, "settings.json");
            if (File.Exists(oldConfig) && !File.Exists(Paths.Config))
                File.Copy(oldConfig, Paths.Config);

            MoveStartup();

            File.WriteAllText(marker,
                $"Favourites moved to {Paths.Dir} on {DateTimeOffset.Now:u}.{Environment.NewLine}");
            Log.Write($"migrated from Heartmark: {kept.Count} favourites moved, {lost} no longer tagged or missing");
        }
        catch (Exception ex)
        {
            // No marker written, so the next start tries again. The old tags are only
            // ever removed after the new one is in place, so nothing is lost meanwhile.
            Log.Write($"migration from Heartmark failed, will retry next start: {ex.Message}");
        }
    }

    /// <summary>
    /// If Heartmark was set to start at sign-in, its Run entry now points at an exe
    /// that is gone. Swap it for ours so the user's choice carries over.
    /// </summary>
    private static void MoveStartup()
    {
        using Microsoft.Win32.RegistryKey? k =
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k?.GetValue(OldRunValue) is null) return;

        k.DeleteValue(OldRunValue, throwOnMissingValue: false);
        Settings.SetStartup(true);
    }
}
