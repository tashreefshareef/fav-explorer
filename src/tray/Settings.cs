using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FavExplorer;


/// <summary>A hotkey as the user thinks of it: some modifiers plus one key.</summary>
internal sealed record Hotkey(bool Ctrl, bool Shift, bool Alt, Keys Key)
{
    public static readonly Hotkey Default = new(true, true, false, Keys.F);

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Default;
        bool ctrl = false, shift = false, alt = false;
        Keys key = Keys.None;

        foreach (string tokenRaw in s.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = tokenRaw.Trim();
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (Enum.TryParse<Keys>(token, true, out Keys k)) key = k;
        }

        // A bare letter with no modifier would break type-to-select in Explorer, so
        // refuse it rather than shipping a shortcut that eats normal typing.
        if (key == Keys.None || (!ctrl && !shift && !alt)) return Default;
        return new Hotkey(ctrl, shift, alt, key);
    }

    /// <summary>
    /// Explorer's own Ctrl+D is Delete. Anyone arriving from a browser will reach for
    /// it to mean "bookmark", so it is refused outright rather than left as a way to
    /// bin a folder of photos by muscle memory.
    /// </summary>
    public bool IsDangerous => Ctrl && !Shift && !Alt &&
        Key is Keys.D or Keys.C or Keys.V or Keys.X or Keys.Z or Keys.A or Keys.N or Keys.W;
}

internal sealed class Settings
{
    [JsonPropertyName("hotkey")]
    public string HotkeyText { get; set; } = Hotkey.Default.ToString();

    [JsonPropertyName("showToast")]
    public bool ShowToast { get; set; } = true;

    [JsonPropertyName("runAtStartup")]
    public bool RunAtStartup { get; set; }

    // Off unless the user turns it on: it is the only thing that makes the app go
    // online at all. See UpdateCheck.
    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; set; }

    [JsonPropertyName("lastUpdateCheck")]
    public long LastUpdateCheck { get; set; }

    // The newest version the tray has already announced, so it says so once.
    [JsonPropertyName("announcedVersion")]
    public string? AnnouncedVersion { get; set; }

    // How the favourites window was last left. Strings rather than the enums so an
    // older or newer build reading this file falls back instead of failing.
    [JsonPropertyName("favouritesView")]
    public string FavouritesView { get; set; } = nameof(FavView.Large);

    [JsonPropertyName("favouritesSort")]
    public string FavouritesSort { get; set; } = nameof(FavSort.Added);

    [JsonPropertyName("favouritesSortDescending")]
    public bool FavouritesSortDescending { get; set; } = true;

    [JsonPropertyName("favouritesGroup")]
    public string FavouritesGroup { get; set; } = nameof(FavGroup.Date);

    // Named Shortcut rather than Hotkey so the member never shadows the type inside
    // this class.
    [JsonIgnore]
    public Hotkey Shortcut
    {
        get => Hotkey.Parse(HotkeyText);
        set => HotkeyText = value.ToString();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.Config))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Config)) ?? new Settings();
        }
        catch
        {
            // A corrupt settings file should cost the user their preferences, not the
            // ability to start the app.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Paths.Ensure();
            File.WriteAllText(Paths.Config, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Fav Explorer";

    public static bool IsRegisteredAtStartup()
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is not null;
    }

    public static void SetStartup(bool enabled)
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k is null) return;

        if (enabled)
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            k.SetValue(RunValue, $"\"{exe}\"");
        }
        else
        {
            k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
