using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Citrium.Services;

internal sealed record ReleaseInfo(Version Version, string Url);

/// <param name="Answered">False if GitHub couldn't say, which isn't the same as there being nothing new.</param>
internal readonly record struct UpdateCheck(bool Answered, ReleaseInfo? Newer);

/// <summary>
/// Asks GitHub for the latest release. It only ever points at the release page: Citrium's builds aren't code
/// signed, so quietly replacing the running program with a download isn't something it should do on its own.
/// </summary>
internal static class UpdateService
{
    private const string Repository = "0dm/citrium";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string ReleasePagePrefix = $"https://github.com/{Repository}/releases/";

    public static Version Current { get; } = Trim(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static async Task<UpdateCheck> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Citrium", Current.ToString()));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync(LatestReleaseUrl);
            if (!response.IsSuccessStatusCode)
            {
                // 404 is normal: no releases yet, or the repository isn't public.
                Log.Info($"Update check: GitHub answered {(int)response.StatusCode}");
                return new UpdateCheck(false, null);
            }
            var release = Parse(await response.Content.ReadAsStringAsync());
            return new UpdateCheck(release is not null, release is not null && IsNewer(release.Version, Current) ? release : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"Update check failed: {ex.Message}");
            return new UpdateCheck(false, null);
        }
    }

    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String) return null;

            var page = url.GetString() ?? "";
            if (!page.StartsWith(ReleasePagePrefix, StringComparison.OrdinalIgnoreCase)) return null;
            if (!Version.TryParse((tag.GetString() ?? "").TrimStart('v', 'V'), out var version)) return null;
            return new ReleaseInfo(Trim(version), page);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsNewer(Version candidate, Version current) => Trim(candidate) > Trim(current);

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Major.minor.patch only, so 1.2 and 1.2.0.0 compare as the same version.</summary>
    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
