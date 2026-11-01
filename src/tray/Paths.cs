namespace FavExplorer;

/// <summary>
/// Where Fav Explorer keeps its state. Deliberately free of any UI dependency so the
/// diagnostic tools in tools/ can link the same storage code the app ships.
/// </summary>
internal static class Paths
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FavExplorer");

    public static string Index     => Path.Combine(Dir, "dirs.bin");
    public static string Favorites => Path.Combine(Dir, "favorites.tsv");
    public static string Config    => Path.Combine(Dir, "settings.json");

    public static void Ensure() => Directory.CreateDirectory(Dir);
}
