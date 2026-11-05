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

public sealed record Notification(string Title, string Message, InfoBarSeverity Severity);

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

    public void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Settings = SettingsService.Load();
        foreach (var a in Settings.Accounts)
        {
            try { a.EntryUrl = NormalizeUrl(a.EntryUrl); } catch (FormatException) { }
            Accounts.Add(new AccountState(a));
        }
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

    public void Notify(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Error) =>
        _dispatcher?.TryEnqueue(() => Notified?.Invoke(this, new Notification(title, message, severity)));

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

    public static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new FormatException("Enter a valid store or gateway address, e.g. citrix.contoso.com");
        // A trailing dot (e.g. from copying a sentence) breaks TLS name matching and cookies.
        var host = uri.Host.TrimEnd('.');
        if (host.Length == 0 || !host.Contains('.'))
            throw new FormatException("Enter a valid store or gateway address, e.g. citrix.contoso.com");
        return new UriBuilder(uri) { Host = host }.Uri.ToString();
    }

    public async Task<bool> SignInAsync(AccountState state)
    {
        state.Status = AccountStatus.SigningIn;
        state.Error = null;
        try
        {
            var result = await SignInWindow.SignInAsync(state.Account);
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

        if (state.Client is null && state.Account.StoreWebUrl is not null)
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
        ResourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveResourcesOf(string accountId, bool raise = true)
    {
        for (var i = Resources.Count - 1; i >= 0; i--)
            if (Resources[i].AccountId == accountId) Resources.RemoveAt(i);
        if (raise) ResourcesChanged?.Invoke(this, EventArgs.Empty);
    }

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
        ResourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LaunchAsync(StoreResource resource)
    {
        if (resource.IsLaunching) return;
        var state = Accounts.FirstOrDefault(a => a.Account.Id == resource.AccountId);
        if (state is null) return;

        // Already open in a viewer (possibly hidden)? Bring it back instead of starting a new connection.
        if (resource.IsDesktop && Viewer.ViewerRegistry.Find(resource.Name, state.Name) is { } running)
        {
            Viewer.ViewerRegistry.Show(running);
            return;
        }

        resource.IsLaunching = true;
        try
        {
            if (state.Client is null && !await RefreshAccountAsync(state, interactive: true)) return;

            string ica;
            try
            {
                ica = await state.Client!.GetIcaFileAsync(resource);
            }
            catch (StoreAuthRequiredException)
            {
                if (!await SignInAsync(state)) return;
                ica = await state.Client!.GetIcaFileAsync(resource);
            }

                        // Apps stay seamless: Citrix draws their windows as normal local windows.
            var proc = resource.IsDesktop && Settings.DesktopViewer != "Citrix" && Viewer.ViewerLauncher.IsAvailable(out _)
                ? Viewer.ViewerLauncher.Launch(ica, resource.Name, state.Name, Settings.KeepIcaFiles)
                : HdxEngine.Launch(ica, resource.Name, Settings.HdxEnginePath, Settings.KeepIcaFiles);
            Sessions.Register(proc, resource.Name, state.Name, resource.IsDesktop);
            Notify($"Starting {resource.Name}", "Your session is being prepared…", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Log.Error($"Launch of {resource.Name} failed", ex);
            Notify($"Couldn't start {resource.Name}", ex is HttpRequestException ? "Can't reach the store." : ex.Message);
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
