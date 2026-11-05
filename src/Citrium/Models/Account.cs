namespace Citrium.Models;

/// <summary>
/// A configured Citrix store (StoreFront "Receiver for Web" site, optionally behind a Citrix Gateway).
/// </summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string EntryUrl { get; set; } = "";

    /// <summary>
    /// The discovered Receiver-for-Web base, always ending with '/', e.g.
    /// https://citrix.contoso.com/Citrix/StoreWeb/ . Null until the first successful sign-in.
    /// </summary>
    public string? StoreWebUrl { get; set; }

    public List<string> Favorites { get; set; } = [];

    public DateTimeOffset? LastSignIn { get; set; }
}
