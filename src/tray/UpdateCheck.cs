using System.Net.Http;
using System.Text.Json;

namespace FavExplorer;

internal sealed record UpdateInfo(Version Latest, string Url)
{
    public bool IsNewer => Latest > UpdateCheck.Current;
}

/// <summary>
/// The one network request Fav Explorer can make, and only when the user has turned
/// it on in Settings: ask GitHub which release is the latest. Nothing is sent but
/// the request itself — no identifier, no list of files, no usage.
/// </summary>
internal static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/tashreefshareef/fav-explorer/releases";
    private const string LatestApi = "https://api.github.com/repos/tashreefshareef/fav-explorer/releases/latest";

    // Weekly is often enough for an app that changes this rarely, and keeps it far
    // from GitHub's limit on anonymous requests.
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    public static Version Current { get; } = Trim(typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub refuses requests without one; it names the app and nothing else.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FavExplorer/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The latest published release, or null if GitHub couldn't be asked.</summary>
    public static async Task<UpdateInfo?> FetchAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestApi));
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.TryGetProperty("html_url", out JsonElement u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest)) return null;
            return new UpdateInfo(Trim(latest), url);
        }
        catch (Exception ex)
        {
            Log.Write($"update check failed: {ex.Message}");
            return null;
        }
    }

    public static bool IsDue(Settings s) =>
        s.CheckForUpdates &&
        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(s.LastUpdateCheck) >= Interval;

    // 0.2.1 and 0.2.1.0 are the same release.
    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
