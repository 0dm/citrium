using System.Net;
using System.Text.Json;
using Citrium.Models;

namespace Citrium.Services;

public sealed class StoreAuthRequiredException(string message) : Exception(message);

/// <summary>
/// Talks to a StoreFront "Receiver for Web" site (…/Citrix/&lt;Store&gt;Web/) — the same HTTP API the
/// Citrix web portal uses — to enumerate resources and obtain ICA launch files.
///
/// Authentication itself is performed interactively in <see cref="Views.SignInWindow"/> (WebView2),
/// which supports every method the store offers (forms, Gateway + MFA, SAML/Entra ID, smart card…).
/// The resulting cookies are handed to this client.
/// </summary>
public sealed class StoreFrontClient : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    public Account Account { get; }
    public Uri StoreWeb { get; }
    private bool IsHttps => StoreWeb.Scheme == Uri.UriSchemeHttps;

    public StoreFrontClient(Account account, IEnumerable<Cookie> cookies)
    {
        Account = account;
        StoreWeb = new Uri(account.StoreWebUrl ?? throw new InvalidOperationException("Store URL not discovered yet."));

        foreach (var c in cookies)
        {
            try { _cookies.Add(c); } catch (CookieException) { /* skip malformed */ }
        }
        // Tell StoreFront we have a native client so it never offers the HTML5 / "detect receiver" path.
        SetCookie("CtxsUserPreferredClient", "Native");
        SetCookie("CtxsClientDetectionDone", "true");
        SetCookie("CtxsHasUpgradeBeenShown", "true");

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = false, // a redirect means "go log in at the gateway"
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Citrium/0.1");
    }

    public IEnumerable<Cookie> Cookies => _cookies.GetAllCookies();

    private string? CsrfToken => _cookies.GetCookies(StoreWeb)["CsrfToken"]?.Value;

    private void SetCookie(string name, string value)
    {
        try { _cookies.Add(new Cookie(name, value, "/", StoreWeb.Host) { Secure = IsHttps }); } catch (CookieException) { }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string relative, string accept)
    {
        var req = new HttpRequestMessage(method, new Uri(StoreWeb, relative));
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.Referrer = StoreWeb;
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.Add("X-Citrix-IsUsingHTTPS", IsHttps ? "Yes" : "No");
        if (CsrfToken is { } csrf) req.Headers.Add("Csrf-Token", csrf);
        return req;
    }

    private static void ThrowIfAuthRequired(HttpResponseMessage resp)
    {
        var code = (int)resp.StatusCode;
        if (code is 401 or 403 || (code >= 300 && code < 400))
            throw new StoreAuthRequiredException($"Store returned {code} {resp.ReasonPhrase}.");
        if (resp.Headers.Contains("CitrixWebReceiver-Authenticate"))
            throw new StoreAuthRequiredException("Store requested authentication.");
    }

    /// <summary>Makes sure a CSRF token exists (the web client obtains one from Home/Configuration).</summary>
    private async Task EnsureCsrfAsync(CancellationToken ct)
    {
        if (CsrfToken is not null) return;
        using var req = NewRequest(HttpMethod.Post, "Home/Configuration", "application/xml, text/xml, */*");
        req.Content = new StringContent("");
        using var resp = await _http.SendAsync(req, ct);
        ThrowIfAuthRequired(resp);
    }

    public async Task<List<StoreResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        await EnsureCsrfAsync(ct);

        using var req = NewRequest(HttpMethod.Post, "Resources/List", "application/json, text/javascript, */*; q=0.01");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["format"] = "json",
            ["resourceDetails"] = "Default",
        });
        using var resp = await _http.SendAsync(req, ct);
        ThrowIfAuthRequired(resp);
        resp.EnsureSuccessStatusCode();

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!mediaType.Contains("json") && !body.TrimStart().StartsWith('{'))
            throw new StoreAuthRequiredException("Store did not return a resource list (session probably expired).");

        using var doc = JsonDocument.Parse(body);
        var list = new List<StoreResource>();
        if (!doc.RootElement.TryGetProperty("resources", out var resources)) return list;

        foreach (var r in resources.EnumerateArray())
        {
            var id = Str(r, "id");
            var name = Str(r, "name");
            if (id is null || name is null) continue;

            if (r.TryGetProperty("clienttypes", out var ctypes) && ctypes.ValueKind == JsonValueKind.Array &&
                !ctypes.EnumerateArray().Any(t => string.Equals(t.GetString(), "ica30", StringComparison.OrdinalIgnoreCase)) &&
                Str(r, "launchurl") is null)
                continue;

            list.Add(new StoreResource
            {
                Id = id,
                Name = name,
                Description = Str(r, "description") ?? "",
                Path = Str(r, "path") ?? "",
                IconUrl = Str(r, "iconurl"),
                LaunchUrl = Str(r, "launchurl"),
                LaunchStatusUrl = Str(r, "launchstatusurl"),
                IsDesktop = r.TryGetProperty("isdesktop", out var d) && d.ValueKind == JsonValueKind.True,
                AccountId = Account.Id,
                AccountName = Account.Name,
            });
        }
        return list;
    }

    public async Task<string> GetIcaFileAsync(StoreResource resource, CancellationToken ct = default)
    {
        if (resource.LaunchUrl is null) throw new InvalidOperationException($"'{resource.Name}' cannot be launched (no ICA launch URL).");
        await EnsureCsrfAsync(ct);

        // Newer StoreFront versions want the launch "prepared" first (VDA power-on, etc.).
        if (resource.LaunchStatusUrl is not null)
            await WaitForLaunchReadyAsync(resource, ct);

        var launchId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sep = resource.LaunchUrl.Contains('?') ? '&' : '?';
        var url = $"{resource.LaunchUrl}{sep}CsrfToken={Uri.EscapeDataString(CsrfToken ?? "")}&IsUsingHttps={(IsHttps ? "Yes" : "No")}&launchId={launchId}";

        using var req = NewRequest(HttpMethod.Get, url, "application/x-ica, application/octet-stream, */*");
        using var resp = await _http.SendAsync(req, ct);
        ThrowIfAuthRequired(resp);
        resp.EnsureSuccessStatusCode();

        var ica = await resp.Content.ReadAsStringAsync(ct);
        if (!ica.Contains("[ApplicationServers]", StringComparison.OrdinalIgnoreCase) &&
            !ica.Contains("[WFClient]", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"Unexpected launch response for {resource.Name}: {Truncate(ica, 500)}");
            throw new InvalidOperationException("The store did not return a valid ICA file. The resource may be unavailable or in maintenance.");
        }
        return ica;
    }

    private async Task WaitForLaunchReadyAsync(StoreResource resource, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            using var req = NewRequest(HttpMethod.Post, resource.LaunchStatusUrl!, "application/json, */*");
            req.Content = new StringContent("");
            using var resp = await _http.SendAsync(req, ct);
            ThrowIfAuthRequired(resp);
            if (!resp.IsSuccessStatusCode) return; // older/odd stores: just try the launch

            var body = await resp.Content.ReadAsStringAsync(ct);
            string? status = null;
            var poll = 5;
            string? error = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                status = Str(doc.RootElement, "status");
                error = Str(doc.RootElement, "errorId");
                if (doc.RootElement.TryGetProperty("pollTimeout", out var pt) && pt.TryGetInt32(out var p)) poll = Math.Clamp(p, 1, 30);
            }
            catch (JsonException) { return; }

            switch (status?.ToLowerInvariant())
            {
                case "success": return;
                case "retry" when DateTime.UtcNow < deadline:
                    await Task.Delay(TimeSpan.FromSeconds(poll), ct);
                    continue;
                case "failure":
                    throw new InvalidOperationException(DescribeLaunchError(error));
                default: return;
            }
        }
    }

    private static string DescribeLaunchError(string? errorId) => errorId switch
    {
        "NoMoreActiveSessions" or "CouldNotConnectToWorkstation" => "No desktop is available right now. Try again later.",
        "WorkstationInMaintenance" => "This desktop is in maintenance mode.",
        "UnavailableDesktop" => "This desktop is currently unavailable.",
        "CannotStartApp" or "AppRemoved" => "The application could not be started.",
        null or "" => "The store refused the launch.",
        _ => $"The store refused the launch ({errorId}).",
    };

    public async Task<byte[]?> GetIconAsync(string iconUrl, CancellationToken ct = default)
    {
        try
        {
            using var req = NewRequest(HttpMethod.Get, iconUrl, "image/*");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && type.Length > 0) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task LogoffAsync()
    {
        try
        {
            using var req = NewRequest(HttpMethod.Post, "Authentication/Logoff", "application/xml, text/xml, */*");
            req.Content = new StringContent("");
            using var _ = await _http.SendAsync(req);
        }
        catch (Exception ex)
        {
            Log.Error("Logoff failed", ex);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _http.Dispose();
}
