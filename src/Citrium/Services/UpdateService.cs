using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Citrium.Services;

/// <param name="Sha256">Lowercase hex, as GitHub reports it for the release's own copy of the file.</param>
internal sealed record InstallerAsset(string Url, string Sha256);

/// <param name="Installer">Null if the release has no usable installer for this PC's architecture.</param>
internal sealed record ReleaseInfo(Version Version, string Url, InstallerAsset? Installer = null);

/// <param name="Answered">False if GitHub couldn't say, which isn't the same as there being nothing new.</param>
internal readonly record struct UpdateCheck(bool Answered, ReleaseInfo? Newer);

/// <summary>
/// Asks GitHub for the latest release, and installs it. Citrium's builds aren't code signed, so all that vouches for
/// an installer is that it came from this repository's releases over HTTPS and matches the digest GitHub gives for it.
/// </summary>
internal static class UpdateService
{
    private const string Repository = "0dm/citrium";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string ReleasePagePrefix = $"https://github.com/{Repository}/releases/";

    private const string DownloadPrefix = $"{ReleasePagePrefix}download/";

    public const string LatestReleasePage = $"{ReleasePagePrefix}latest";

    private static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64";

    /// <summary>False for a zip, a development build or the demo, which an installer wouldn't replace.</summary>
    public static bool RunsFromInstall { get; } = FindRunsFromInstall();

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

    public static ReleaseInfo? Parse(string json, string? architecture = null)
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
            return new ReleaseInfo(Trim(version), page, FindInstaller(root, architecture ?? Architecture));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsNewer(Version candidate, Version current) => Trim(candidate) > Trim(current);

    private static InstallerAsset? FindInstaller(JsonElement root, string architecture)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (Text(asset, "name") is not { } name || !name.EndsWith($"-{architecture}.msi", StringComparison.OrdinalIgnoreCase)) continue;
            if (Text(asset, "browser_download_url") is not { } url || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (Text(asset, "digest") is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) continue;
            var hash = digest["sha256:".Length..].ToLowerInvariant();
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) return new InstallerAsset(url, hash);
        }
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>Saves the installer, and returns where, or null if it couldn't be fetched or isn't the file GitHub described.</summary>
    public static async Task<string?> DownloadAsync(InstallerAsset installer)
    {
        var path = Path.Combine(AppPaths.Updates, "Citrium.msi");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Citrium", Current.ToString()));
            await using (var source = await http.GetStreamAsync(installer.Url))
            await using (var file = File.Create(path))
            {
                await source.CopyToAsync(file);
            }
            if (HasHash(path, installer.Sha256)) return path;
            Log.Info("Update download doesn't match its digest");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Log.Info($"Update download failed: {ex.Message}");
        }
        DeleteDownload();
        return null;
    }

    public static bool HasHash(string path, string sha256)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file)) == sha256;
    }

    public static void DeleteDownload()
    {
        try
        {
            File.Delete(Path.Combine(AppPaths.Updates, "Citrium.msi"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Couldn't delete the update download: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the installer outside Citrium, to run once it has quit, and to reopen Citrium whether or not it worked.
    /// False if that couldn't be started, so Citrium should carry on running.
    /// </summary>
    public static bool StartInstall(string installer, string? arguments)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        try
        {
            // Not the install folder as the working directory: that would keep it in use.
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                InstallCommand(Environment.SystemDirectory, installer, Path.Combine(AppPaths.Updates, "install.log"), exe, arguments))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppPaths.Updates,
            })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Couldn't start the update", ex);
            return false;
        }
    }

    /// <summary>
    /// The ping is a pause for Citrium to exit. Restart Manager stays off so that if a session started at the last
    /// moment, the installer leaves it running rather than closing it.
    /// </summary>
    public static string InstallCommand(string system, string installer, string log, string exe, string? arguments) =>
        $"/d /s /c \"\"{system}\\ping.exe\" -n 3 127.0.0.1 >nul " +
        $"& start \"\" /wait \"{system}\\msiexec.exe\" /i \"{installer}\" /qn /norestart MSIRESTARTMANAGERCONTROL=Disable /l*v \"{log}\" " +
        $"& start \"\" \"{exe}\" {arguments}\"";

    private static bool FindRunsFromInstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Citrium");
        return key?.GetValue("InstallFolder") is string folder && string.Equals(
            Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Today, 11:07 AM", in <paramref name="now"/>'s time zone. Older than yesterday, it gives the date.</summary>
    public static string DescribeLastCheck(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToOffset(now.Offset);
        var day = local.Date == now.Date ? "Today"
            : local.Date == now.Date.AddDays(-1) ? "Yesterday"
            : local.ToString("d");
        return $"{day}, {local:t}";
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Major.minor.patch only, so 1.2 and 1.2.0.0 compare as the same version.</summary>
    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
