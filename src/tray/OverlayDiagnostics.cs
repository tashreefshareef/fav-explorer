using Microsoft.Win32;

namespace FavExplorer;

internal sealed record OverlayStatus(
    bool Registered,
    bool DllPresent,
    string? DllPath,
    int Position,      // 1-based rank among all registered handlers, -1 if absent
    int Total,
    int UsableSlots)
{
    public bool HasSlot => Registered && DllPresent && Position > 0 && Position <= UsableSlots;

    public string Headline => !Registered ? "Not installed"
                            : !DllPresent ? "Installed, but the handler file is missing"
                            : HasSlot     ? "Working"
                                          : "Registered, but Windows has run out of slots";
}

/// <summary>
/// Windows keeps a fixed, global list of icon overlay handlers and only honours the
/// first handful. It sorts them by key name and silently ignores the losers, which
/// makes "my badge stopped appearing" almost impossible to diagnose from the outside.
///
/// So the app checks its own ranking and says where it stands, rather than leaving
/// the user to guess why nothing happened.
/// </summary>
internal static class OverlayDiagnostics
{
    private const string OverlayRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";

    public const string OurKeyName = "   FavExplorer";

    // The shell publishes 15 slots and takes several for its own use (link and share
    // arrows, and the "slow file" badge). What is left in practice is around eleven,
    // and that is what the warning is pitched against.
    public const int UsableSlots = 11;

    public static OverlayStatus Read()
    {
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(OverlayRoot);
            if (root is null) return new OverlayStatus(false, false, null, -1, 0, UsableSlots);

            string[] names = root.GetSubKeyNames();

            // Explorer's ordering is an ordinal sort on the key name. Leading spaces
            // sort before everything, which is exactly why our key has three.
            Array.Sort(names, StringComparer.Ordinal);

            int position = Array.IndexOf(names, OurKeyName) + 1;
            if (position == 0)
                return new OverlayStatus(false, false, null, -1, names.Length, UsableSlots);

            using RegistryKey? ours = root.OpenSubKey(OurKeyName);
            string? clsid = ours?.GetValue(null) as string;

            string? dll = null;
            if (!string.IsNullOrEmpty(clsid))
            {
                using RegistryKey? inproc = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Classes\CLSID\{clsid}\InprocServer32");
                dll = inproc?.GetValue(null) as string;
            }

            bool present = !string.IsNullOrEmpty(dll) && File.Exists(dll);
            return new OverlayStatus(true, present, dll, position, names.Length, UsableSlots);
        }
        catch
        {
            return new OverlayStatus(false, false, null, -1, 0, UsableSlots);
        }
    }

    /// <summary>The competing handlers, in the order Windows considers them.</summary>
    public static List<(string Name, bool Ours, bool Honoured)> ListHandlers()
    {
        var list = new List<(string, bool, bool)>();
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(OverlayRoot);
            if (root is null) return list;

            string[] names = root.GetSubKeyNames();
            Array.Sort(names, StringComparer.Ordinal);

            for (int i = 0; i < names.Length; i++)
                list.Add((names[i], names[i] == OurKeyName, i < UsableSlots));
        }
        catch
        {
        }
        return list;
    }
}
