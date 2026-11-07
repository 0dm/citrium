using Citrium.Models;
using Citrium.Services;

namespace Citrium.Demo;

/// <summary>Acme, the made-up company a demo build belongs to: its accounts, apps, desktops and logo.</summary>
internal static class DemoWorld
{
    public const string StoreWebUrl = "https://apps.acme.example/Citrix/StoreWeb/";

    private const string HeadOffice = "acme", Labs = "acme-labs";

    private sealed record Item(string Id, string Name, string Folder, string Description, bool Desktop = false, bool Favorite = false);

    private static readonly Dictionary<string, Item[]> Catalog = new()
    {
        [HeadOffice] =
        [
            new("engineering", "Engineering Desktop", "Desktops", "Your everyday development machine.", true, true),
            new("design", "Design Workstation", "Desktops", "GPU-backed, with the whole asset library.", true, true),
            new("finance", "Finance Desktop", "Desktops", "Locked down for quarter close.", true),
            new("mail", "Mail", "Office", "Your Acme inbox.", Favorite: true),
            new("calendar", "Calendar", "Office", "Meetings, rooms and time off."),
            new("docs", "Docs", "Office", "The company handbook and everything else."),
            new("chat", "Team Chat", "Office", "Say hello."),
            new("sheets", "Sheets", "Office", "Rows upon rows."),
            new("code", "Code Review", "Engineering", "Pull requests waiting on you."),
            new("builds", "Build Monitor", "Engineering", "Everything's green. Mostly."),
            new("time", "Timesheets", "People", "Due Friday."),
            new("expenses", "Expenses", "People", "Yes, you need the receipt."),
            new("support", "Help Desk", "People", "Have you tried turning it off and on again?"),
        ],
        [Labs] =
        [
            new("lab", "Test Lab Desktop", "", "A clean machine, reset every night.", true),
            new("rockets", "Rocket Telemetry", "", "Mostly upward."),
        ],
    };

    public static void Seed(AppState state)
    {
        state.Settings.CheckForUpdates = false;
        if (state.Accounts.Count == 0)
        {
            state.Accounts.Add(new AccountState(NewAccount(HeadOffice, "Acme", "https://apps.acme.example/")));
            state.Accounts.Add(new AccountState(NewAccount(Labs, "Acme Labs", "https://labs.acme.example/")));
            state.SaveSettings();
        }

        // Always signed in: there's nobody to sign in to.
        foreach (var account in state.Accounts) account.Client = new StoreFrontClient(account.Account, []);

        var branding = Directory.CreateDirectory(Path.Combine(AppPaths.Root, "Branding")).FullName;
        foreach (var theme in new[] { "light", "dark" })
            File.Copy(Asset($"logo-{theme}.png"), Path.Combine(branding, $"{HeadOffice}-demo-{theme}.png"), overwrite: true);
    }

    private static Account NewAccount(string id, string name, string url) => new()
    {
        Id = id,
        Name = name,
        EntryUrl = url,
        StoreWebUrl = StoreWebUrl,
        LastSignIn = DateTimeOffset.Now,
        Favorites = Catalog[id].Where(i => i.Favorite).Select(i => i.Id).ToList(),
    };

    public static List<StoreResource> ResourcesFor(Account account) =>
        (Catalog.TryGetValue(account.Id, out var items) ? items : Catalog[HeadOffice]).Select(i => new StoreResource
        {
            Id = i.Id,
            Name = i.Name,
            Description = i.Description,
            Path = i.Folder,
            IconUrl = i.Id,
            LaunchUrl = i.Id,
            IsDesktop = i.Desktop,
            AccountId = account.Id,
            AccountName = account.Name,
        }).ToList();

    /// <summary>A launch file like a store's, naming what the fake machine should be.</summary>
    public static string LaunchFileFor(StoreResource resource) => $"""
        [WFClient]
        Version=2
        [ApplicationServers]
        Demo=
        [Demo]
        Title={resource.Name}
        Desktop={(resource.IsDesktop ? "On" : "Off")}
        Scene={resource.Id}
        """;

    public static async Task<byte[]?> IconAsync(string url, CancellationToken ct)
    {
        var path = Asset(Path.Combine("icons", url.Split('?')[0] + ".png"));
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Demo", "Assets", name);
}
