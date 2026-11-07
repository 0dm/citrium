using System.Text.RegularExpressions;

namespace Citrium.Services;

/// <summary>
/// A link to one published app or desktop: <c>citrium://open/&lt;account&gt;/&lt;resource&gt;</c>. Jump lists, desktop
/// shortcuts and Reconnect all open resources this way. An account's id is a random GUID that only exists on this
/// PC, so a web page can't guess a link that opens anything.
/// </summary>
internal readonly partial record struct CitriumLink(string AccountId, string ResourceId, bool FullScreen = false)
{
    public const string Scheme = "citrium";
    private const string Prefix = Scheme + "://open/";
    private const string FullScreenFlag = "fullscreen=1";
    private const int MaxLength = 2048;

    public string ToUri()
    {
        var uri = $"{Prefix}{Uri.EscapeDataString(AccountId)}/{Uri.EscapeDataString(ResourceId)}";
        return FullScreen ? $"{uri}?{FullScreenFlag}" : uri;
    }

    public static bool TryParse(string? text, out CitriumLink link)
    {
        link = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return false;
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = text[Prefix.Length..];
        var query = "";
        var mark = rest.IndexOf('?');
        if (mark >= 0) (rest, query) = (rest[..mark], rest[(mark + 1)..]);

        var parts = rest.TrimEnd('/').Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;

        link = new CitriumLink(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]),
            query.Split('&').Contains(FullScreenFlag, StringComparer.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>Finds a link in a command line, which also carries the program's own path.</summary>
    public static bool TryFind(string? commandLine, out CitriumLink link)
    {
        link = default;
        return commandLine is not null && LinkPattern().Match(commandLine) is { Success: true } match && TryParse(match.Value, out link);
    }

    [GeneratedRegex(@"citrium://[^\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();
}
