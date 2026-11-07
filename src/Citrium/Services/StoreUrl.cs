namespace Citrium.Services;

internal static class StoreUrl
{
    /// <summary>Tidies an address the user typed into a full HTTPS URL, or throws a message that's fit to show them.</summary>
    public static string Normalize(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Citrium only connects to stores over HTTPS.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException("Enter a valid store or gateway address, e.g. citrix.contoso.com");
        // A trailing dot (e.g. from copying a sentence) breaks TLS name matching and cookies.
        var host = uri.Host.TrimEnd('.');
        if (host.Length == 0 || !host.Contains('.'))
            throw new FormatException("Enter a valid store or gateway address, e.g. citrix.contoso.com");
        return new UriBuilder(uri) { Host = host }.Uri.ToString();
    }
}
