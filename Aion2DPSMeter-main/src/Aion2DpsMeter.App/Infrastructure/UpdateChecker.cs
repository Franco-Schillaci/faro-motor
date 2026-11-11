using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Aion2DpsMeter.App.Infrastructure;

public sealed record UpdateInfo(Version Version, string Tag, string Url);

/// <summary>
/// Looks for a newer release on GitHub. Game patches renumber opcodes, so an old meter silently
/// stops counting; telling people about updates matters more than for most tools. It only reads the
/// public release list and never downloads or installs anything.
/// </summary>
public static class UpdateChecker
{
    /// <summary>"owner/repo" of the GitHub repository that publishes releases. Empty disables the check.</summary>
    public const string Repository = "domel1998/Aion2DPSMeter";

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>The latest release when it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Repository))
            return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Aion2DpsMeter/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (!response.IsSuccessStatusCode)
            return null; // no release yet, or rate limited
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        string url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
        if (!TryParseTag(tag, out var version))
            return null;
        return version > CurrentVersion ? new UpdateInfo(version, tag, url) : null;
    }

    /// <summary>"v0.2.1" or "0.2.1" → 0.2.1.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0)
            s = s[..dash];
        if (Version.TryParse(s, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }
}
