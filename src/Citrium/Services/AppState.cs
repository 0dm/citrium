using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using Citrium.Models;
using Citrium.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Citrium.Services;

public enum AccountStatus { NotSignedIn, SigningIn, Refreshing, Ready, Error }

public enum UpdateProgress { None, Downloading, WaitingForSessions }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AccountState : INotifyPropertyChanged
{
    public AccountState(Account account) => Account = account;

    public Account Account { get; }
    public StoreFrontClient? Client { get; set; }

    public string Name => Account.Name;
    public string Url => Account.StoreWebUrl ?? Account.EntryUrl;

    private AccountStatus _status;
    public AccountStatus Status
    {
        get => _status;
        set { _status = value; OnChanged(); OnChanged(nameof(StatusText)); OnChanged(nameof(IsSignedIn)); OnChanged(nameof(IsBusy)); }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { _error = value; OnChanged(); OnChanged(nameof(StatusText)); }
    }

    private int _count;
    public int ResourceCount
    {
        get => _count;
        set { _count = value; OnChanged(); OnChanged(nameof(StatusText)); }
    }

    public bool IsSignedIn => Status == AccountStatus.Ready;
    public bool IsBusy => Status is AccountStatus.SigningIn or AccountStatus.Refreshing;

    public string StatusText => Status switch
    {
        AccountStatus.NotSignedIn => "Not signed in",
        AccountStatus.SigningIn => "Signing in…",
        AccountStatus.Refreshing => "Refreshing…",
        AccountStatus.Ready => $"Signed in · {ResourceCount} resource{(ResourceCount == 1 ? "" : "s")}",
        AccountStatus.Error => $"Error: {Error}",
        _ => "",
    };

    public void NotifyNameChanged() { OnChanged(nameof(Name)); OnChanged(nameof(Url)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

public sealed record Notification(string Title, string Message, InfoBarSeverity Severity, string? ActionLabel = null, Action? Action = null);

public sealed class AppState
{
    public static AppState Current { get; } = new();

    private DispatcherQueue _dispatcher = null!;

    public AppSettings Settings { get; private set; } = new();
    public ObservableCollection<AccountState> Accounts { get; } = [];
    public ObservableCollection<StoreResource> Resources { get; } = [];
    public SessionMonitor Sessions { get; private set; } = null!;

    public event EventHandler? ResourcesChanged;
    public event EventHandler? BrandingChanged;

    /// <summary>The logo to show: the first account (in list order) whose store publishes one.</summary>
    public AccountLogo? OrganizationLogo =>
        Accounts.Select(a => BrandingService.Cached(a.Account)).FirstOrDefault(l => l is not null);
    public event EventHandler<Notification>? Notified;

    public bool CheckingForUpdates { get; private set; }

    /// <summary>The last check got no answer from GitHub. Only true until the next check, or until Citrium restarts.</summary>
    public bool UpdateCheckFailed { get; private set; }

    public event EventHandler? UpdateStatusChanged;

    /// <summary>What the last check found. Not saved, unlike the version: it's only good for this release.</summary>
    internal ReleaseInfo? LatestRelease { get; private set; }

    public UpdateProgress UpdateProgress
    {
        get;
        set
        {
            field = value;
            UpdateStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The latest version GitHub told us about, if it's newer than this one.</summary>
    public Version? AvailableUpdate =>
        Version.TryParse(Settings.LatestVersion, out var version) && UpdateService.IsNewer(version, UpdateService.Current) ? version : null;

    /// <summary>Returns the release if there's a newer one. Does nothing if a check is already running.</summary>
    internal async Task<ReleaseInfo?> CheckForUpdatesAsync()
    {
        if (CheckingForUpdates) return null;
        CheckingForUpdates = true;
        UpdateStatusChanged?.Invoke(this, EventArgs.Empty);

        var check = await UpdateService.CheckAsync();

        CheckingForUpdates = false;
        UpdateCheckFailed = !check.Answered;
        if (check.Answered)
        {
            Settings.LastUpdateCheck = DateTimeOffset.Now;
            Settings.LatestVersion = check.Newer?.Version.ToString();
            LatestRelease = check.Newer;
            SaveSettings();
        }
        UpdateStatusChanged?.Invoke(this, EventArgs.Empty);
        return check.Newer;
    }

    public void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Settings = SettingsService.Load();
        foreach (var a in Settings.Accounts)
        {
            try { a.EntryUrl = NormalizeUrl(a.EntryUrl); } catch (FormatException) { }
            Accounts.Add(new AccountState(a));
        }
#if DEMO
        Demo.DemoWorld.Seed(this);
#endif
        Sessions = new SessionMonitor(dispatcher);
        HdxEngine.CleanupOldFiles();
    }

    public void SaveSettings()
    {
        Settings.Accounts = Accounts.Select(a => a.Account).ToList();
        SettingsService.Save(Settings);
    }

    /// <summary>Viewer processes only need settings (theme); no accounts, sessions or timers.</summary>
    public void LoadSettings() => Settings = SettingsService.Load();

    public void Notify(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Error, string? actionLabel = null, Action? action = null) =>
        _dispatcher?.TryEnqueue(() => Notified?.Invoke(this, new Notification(title, message, severity, actionLabel, action)));

    public async Task<AccountState?> AddAccountAsync(string name, string url)
    {
        url = NormalizeUrl(url);
        var account = new Account
        {
            Name = string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name.Trim(),
            EntryUrl = url,
        };
        var state = new AccountState(account);
        Accounts.Add(state);
        SaveSettings();

        if (!await SignInAsync(state))
        {
            // Keep it: the user can retry from the Accounts page.
            state.Status = AccountStatus.NotSignedIn;
        }
        return state;
    }

    public async Task RemoveAccountAsync(AccountState state)
    {
        if (state.Client is not null) await state.Client.LogoffAsync();
        state.Client?.Dispose();
        CookieVault.Delete(state.Account.Id);
        await SignInWindow.ClearBrowserDataAsync();
        BrandingService.Delete(state.Account);
        Accounts.Remove(state);
        BrandingChanged?.Invoke(this, EventArgs.Empty);
        RemoveResourcesOf(state.Account.Id);
        SaveSettings();
    }

    public async Task SignOutAsync(AccountState state)
    {
        if (state.Client is not null) await state.Client.LogoffAsync();
        state.Client?.Dispose();
        state.Client = null;
        CookieVault.Delete(state.Account.Id);
        await SignInWindow.ClearBrowserDataAsync();
        state.Status = AccountStatus.NotSignedIn;
        RemoveResourcesOf(state.Account.Id);
    }

    public static string NormalizeUrl(string url) => StoreUrl.Normalize(url);

    public async Task<bool> SignInAsync(AccountState state)
    {
        state.Status = AccountStatus.SigningIn;
        state.Error = null;
        try
        {
#if DEMO
            var result = new SignInResult(Demo.DemoWorld.StoreWebUrl, []);
#else
            var result = await SignInWindow.SignInAsync(state.Account);
#endif
            if (result is null)
            {
                state.Status = AccountStatus.NotSignedIn;
                return false;
            }

            state.Account.StoreWebUrl = result.StoreWebUrl;
            state.Account.LastSignIn = DateTimeOffset.Now;
            state.NotifyNameChanged();
            SaveSettings();

            state.Client?.Dispose();
            state.Client = new StoreFrontClient(state.Account, result.Cookies);
            await CookieVault.SaveAsync(state.Account.Id, state.Client.Cookies);
            return await RefreshAccountAsync(state, interactive: false);
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in failed", ex);
            state.Status = AccountStatus.Error;
            state.Error = ex.Message;
            Notify($"Couldn't sign in to {state.Name}", ex.Message);
            return false;
        }
    }

    public async Task RefreshAllAsync(bool interactive)
    {
        foreach (var a in Accounts.ToList())
            await RefreshAccountAsync(a, interactive);
    }

    public async Task<bool> RefreshAccountAsync(AccountState state, bool interactive)
    {
        if (state.IsBusy && state.Status != AccountStatus.SigningIn) return false;

        if (state.Client is null && state.Account.StoreWebUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
        {
            var cookies = await CookieVault.LoadAsync(state.Account.Id);
            if (cookies.Count > 0) state.Client = new StoreFrontClient(state.Account, cookies);
        }

        if (state.Client is null)
        {
            if (interactive) return await SignInAsync(state);
            state.Status = AccountStatus.NotSignedIn;
            return false;
        }

        state.Status = AccountStatus.Refreshing;
        try
        {
            var list = await state.Client.ListResourcesAsync();
            ApplyResources(state, list);
            state.ResourceCount = list.Count;
            state.Status = AccountStatus.Ready;
            _ = RefreshBrandingAsync(state.Account);
            await CookieVault.SaveAsync(state.Account.Id, state.Client.Cookies);
            return true;
        }
        catch (StoreAuthRequiredException)
        {
            state.Client.Dispose();
            state.Client = null;
            CookieVault.Delete(state.Account.Id);
            if (interactive) return await SignInAsync(state);
            state.Status = AccountStatus.NotSignedIn;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Refresh of {state.Name} failed", ex);
            state.Status = AccountStatus.Error;
            state.Error = ex is HttpRequestException ? "Can't reach the store." : ex.Message;
            return false;
        }
    }

    private void ApplyResources(AccountState state, List<StoreResource> list)
    {
        RemoveResourcesOf(state.Account.Id, raise: false);
        var favs = state.Account.Favorites.ToHashSet();
        foreach (var r in list.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            r.IsFavorite = favs.Contains(r.Id);
            Resources.Add(r);
            _ = IconCache.LoadAsync(r, state.Client);
        }
        RaiseResourcesChanged();
    }

    private void RemoveResourcesOf(string accountId, bool raise = true)
    {
        for (var i = Resources.Count - 1; i >= 0; i--)
            if (Resources[i].AccountId == accountId) Resources.RemoveAt(i);
        if (raise) RaiseResourcesChanged();
    }

    private void RaiseResourcesChanged()
    {
        ResourcesChanged?.Invoke(this, EventArgs.Empty);
        UpdateJumpList();
    }

    private List<JumpListItem>? _jumpList;

    private void UpdateJumpList()
    {
        var items = Resources.Where(r => r.IsFavorite).Select(r => new JumpListItem(r.Name, LinkTo(r).ToUri())).ToList();
        if (_jumpList?.SequenceEqual(items) == true) return;
        _jumpList = items;
        JumpList.Update(items);
    }

    internal static CitriumLink LinkTo(StoreResource resource, bool fullScreen = false) => new(resource.AccountId, resource.Id, fullScreen);

    private async Task RefreshBrandingAsync(Account account)
    {
        var before = BrandingService.Cached(account);
        var after = await BrandingService.RefreshAsync(account);
        if (after != before) BrandingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleFavorite(StoreResource resource)
    {
        var state = Accounts.FirstOrDefault(a => a.Account.Id == resource.AccountId);
        if (state is null) return;
        resource.IsFavorite = !resource.IsFavorite;
        state.Account.Favorites.Remove(resource.Id);
        if (resource.IsFavorite) state.Account.Favorites.Add(resource.Id);
        SaveSettings();
        RaiseResourcesChanged();
    }

    /// <summary>Opens what a link points to. False if it couldn't, after saying why.</summary>
    internal async Task<bool> OpenLinkAsync(CitriumLink link)
    {
        var state = Accounts.FirstOrDefault(a => a.Account.Id == link.AccountId);
        if (state is null)
        {
            Notify("That link doesn't lead anywhere", "It's for an account that isn't on this PC.");
            return false;
        }

        StoreResource? Find() => Resources.FirstOrDefault(r => r.AccountId == link.AccountId && r.Id == link.ResourceId);

        // At startup the first refresh is still running, and a busy account won't refresh again.
        for (var i = 0; i < 300 && state.IsBusy; i++) await Task.Delay(100);
        if (Find() is null) await RefreshAccountAsync(state, interactive: true);

        if (Find() is { } resource) return await LaunchAsync(resource, link.FullScreen);
        if (state.IsSignedIn) Notify("That link doesn't lead anywhere", $"{state.Name} no longer offers it.");
        return false;
    }

    /// <summary>True once the session is on its way, or was already open.</summary>
    public async Task<bool> LaunchAsync(StoreResource resource, bool fullScreen = false)
    {
        if (resource.IsLaunching) return false;
        var state = Accounts.FirstOrDefault(a => a.Account.Id == resource.AccountId);
        if (state is null) return false;

        // Already open in a viewer (possibly hidden)? Bring it back instead of starting a new connection.
        var open = resource.IsDesktop ? Viewer.ViewerRegistry.Find(resource.Name, state.Name) : null;
        if (open is { Ended: false })
        {
            Viewer.ViewerRegistry.Show(open);
            return true;
        }

        resource.IsLaunching = true;
        try
        {
            if (state.Client is null && !await RefreshAccountAsync(state, interactive: true)) return false;

            string ica;
            try
            {
                ica = await state.Client!.GetIcaFileAsync(resource);
            }
            catch (StoreAuthRequiredException)
            {
                if (!await SignInAsync(state)) return false;
                ica = await state.Client!.GetIcaFileAsync(resource);
            }

            // Only now that there's a ticket: until then, the old window is what's telling the user what happened.
            if (open is not null) Viewer.ViewerRegistry.RequestDisconnect(open.ViewerPid);

            // Apps stay seamless: Citrix draws their windows as normal local windows.
            var proc = resource.IsDesktop && Settings.DesktopViewer != "Citrix" && Viewer.ViewerLauncher.IsAvailable(out _)
                ? Viewer.ViewerLauncher.Launch(ica, resource.Name, state.Name, LinkTo(resource, fullScreen), Settings.KeepIcaFiles)
                : HdxEngine.Launch(ica, resource.Name, Settings.HdxEnginePath, Settings.KeepIcaFiles);
            Sessions.Register(proc, resource.Name, state.Name, resource.IsDesktop);
            Notify($"Starting {resource.Name}", "Your session is being prepared…", InfoBarSeverity.Informational);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Launch of {resource.Name} failed", ex);
            Notify($"Couldn't start {resource.Name}", ex is HttpRequestException ? "Can't reach the store." : ex.Message);
            return false;
        }
        finally
        {
            // Keep the spinner briefly so the click feels acknowledged while the engine window appears.
            await Task.Delay(1500);
            resource.IsLaunching = false;
        }
    }

    public void LaunchIcaFile(string path)
    {
        try
        {
            var proc = HdxEngine.LaunchFile(path, HdxEngine.Locate(Settings.HdxEnginePath));
            Sessions.Register(proc, Path.GetFileNameWithoutExtension(path), null, false);
        }
        catch (Exception ex)
        {
            Notify("Couldn't open ICA file", ex.Message);
        }
    }
}

public sealed record SignInResult(string StoreWebUrl, List<Cookie> Cookies);
