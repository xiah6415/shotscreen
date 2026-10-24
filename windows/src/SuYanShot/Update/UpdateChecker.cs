using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuYanShot.Update;

/// The newest GitHub Release, as far as the Windows update flow needs it.
internal sealed record ReleaseInfo(string Version, string Notes, string PageUrl, string? ExeUrl, string? ChecksumUrl);

internal static class AppVersion
{
    /// The running app's version (from the repository's VERSION file at build time, without the "+commit" suffix).
    public static string Current
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }
    }

    /// Numeric dot-separated comparison, as on macOS: "v1.10" is newer than "1.9", "1.2" equals "1.2.0",
    /// a pre-release suffix ("1.2.0-beta") is ignored.
    public static bool IsNewer(string candidate, string installed)
    {
        var a = Components(candidate);
        var b = Components(installed);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    internal static List<int> Components(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) trimmed = trimmed[1..];
        var end = trimmed.IndexOfAny(new[] { '-', '+' });
        if (end >= 0) trimmed = trimmed[..end];
        return trimmed.Split('.').Select(part => int.TryParse(part, out var n) ? n : 0).ToList();
    }
}

/// Asks the GitHub Releases API for the latest published release, the same feed the macOS app reads.
/// The Windows build is the release asset named `Glean-<version>-win-x64.exe` (not a .zip, so the macOS updater,
/// which takes the first .zip, never picks it), with its SHA-256 in a matching `.sha256` asset.
internal sealed class UpdateChecker
{
    public const string Repository = "xiah6415/shotscreen";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";
    private const string LatestRelease = $"https://api.github.com/repos/{Repository}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// Developer override for testing against a local feed (settings.json "updateFeedUrl").
    private readonly string feedUrl;

    public UpdateChecker(string? feedOverride = null) => feedUrl = string.IsNullOrWhiteSpace(feedOverride) ? LatestRelease : feedOverride;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Glean-Windows/{AppVersion.Current}");
        return client;
    }

    internal static HttpClient Client => Http;

    /// Null when the repository has no release yet.
    public async Task<ReleaseInfo?> LatestReleaseAsync(CancellationToken token = default)
    {
        using var response = await Http.GetAsync(feedUrl, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub 回應了 {(int)response.StatusCode}。");
        return Parse(await response.Content.ReadAsByteArrayAsync(token));
    }

    private sealed record Asset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string Url);

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("assets")] List<Asset>? Assets);

    internal static ReleaseInfo Parse(byte[] json)
    {
        var body = json.AsSpan();
        if (body.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) body = body[3..]; // tolerate a UTF-8 BOM (local test feeds)
        var release = JsonSerializer.Deserialize<Release>(body) ?? throw new JsonException("empty release");
        var version = release.TagName.TrimStart('v', 'V');
        var assets = release.Assets ?? new();
        var exe = assets.FirstOrDefault(a => a.Name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase));
        var checksum = exe == null ? null : assets.FirstOrDefault(a => a.Name.Equals(exe.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        return new ReleaseInfo(version, release.Body?.Trim() ?? "", release.HtmlUrl, exe?.Url, checksum?.Url);
    }
}
