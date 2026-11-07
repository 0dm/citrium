using System.Net;
using Citrium.Models;
using Citrium.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Citrium.Views;

/// <summary>
/// Hosts the store's own web logon page in WebView2. The user authenticates with whatever the site
/// requires; as soon as the portal successfully calls <c>Resources/List</c> we know the session is live,
/// learn the real Receiver-for-Web base URL from that request, harvest the cookies and close.
/// </summary>
public sealed partial class SignInWindow : Window
{
    private static Task<CoreWebView2Environment>? _environment;
    private static string ClearFlag => Path.Combine(AppPaths.Root, "clear-webview.flag");

    private readonly Account _account;
    private readonly TaskCompletionSource<SignInResult?> _tcs = new();
    private bool _completed;
    private readonly HashSet<string> _askedHosts = new(StringComparer.OrdinalIgnoreCase);

    private SignInWindow(Account account)
    {
        _account = account;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        Title = $"Sign in to {account.Name}";
        AppTitleBar.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Citrium.ico"));
        WindowHelper.ResizeCentered(this, 980, 760);
        WindowHelper.ApplyTheme(this, Root);

        Closed += (_, _) => Complete(null);
        Activated += OnFirstActivated;
    }

    public static Task<SignInResult?> SignInAsync(Account account)
    {
        var window = new SignInWindow(account);
        window.Activate();
        return window._tcs.Task;
    }

    /// <summary>
    /// Deletes the embedded browser's profile, including identity provider cookies. Its processes can outlive the
    /// sign-in window briefly, so this retries, and the flag finishes the job on next use if the files stay locked.
    /// </summary>
    public static async Task ClearBrowserDataAsync()
    {
        try { File.WriteAllText(ClearFlag, "1"); } catch { }
        _environment = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await Task.Run(() =>
                {
                    Directory.Delete(AppPaths.WebView2Data, recursive: true);
                    File.Delete(ClearFlag);
                });
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
        }
        Log.Info("Browser data is in use; it will be cleared the next time sign-in opens");
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        try
        {
            _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, AppPaths.WebView2Data, new CoreWebView2EnvironmentOptions()).AsTask();
            await Web.EnsureCoreWebView2Async(await _environment);
            var core = Web.CoreWebView2;

            if (File.Exists(ClearFlag))
            {
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage);
                File.Delete(ClearFlag);
            }

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;

            core.WebResourceResponseReceived += OnResponseReceived;
            core.NavigationStarting += (_, a) => { Loading.Visibility = Visibility.Visible; ShowAddress(a.Uri); };
            core.NavigationCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
            core.SourceChanged += (_, _) => ShowAddress(core.Source);
            // Keep popups (e.g. IdP pages) in this window.
            core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };
            // The portal tries to hand launches to receiver:// — Citrium does that itself.
            core.LaunchingExternalUriScheme += (_, a) => a.Cancel = true;

            SeedClientDetectionCookies(core, new Uri(_account.StoreWebUrl ?? _account.EntryUrl));
            core.Navigate(_account.StoreWebUrl ?? _account.EntryUrl);
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 init failed", ex);
            AppState.Current.Notify("Sign-in unavailable",
                "The Microsoft Edge WebView2 Runtime is required for signing in. " + ex.Message);
            Close();
        }
    }

    /// <summary>Skips the "Detect Citrix Workspace app" / "Use light version" interstitial.</summary>
    private static void SeedClientDetectionCookies(CoreWebView2 core, Uri uri)
    {
        foreach (var (name, value) in new[] { ("CtxsUserPreferredClient", "Native"), ("CtxsClientDetectionDone", "true"), ("CtxsHasUpgradeBeenShown", "true") })
        {
            var c = core.CookieManager.CreateCookie(name, value, uri.Host, "/");
            c.IsSecure = uri.Scheme == "https";
            core.CookieManager.AddOrUpdateCookie(c);
        }
    }

    private void ShowAddress(string uri)
    {
        var secure = uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
        HostText.Text = Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.Host : uri;
        LockIcon.Glyph = secure ? "" : "";
        ToolTipService.SetToolTip(AddressPill, new ToolTip { Content = new TextBlock { Text = uri, TextWrapping = TextWrapping.Wrap, MaxLines = 6, TextTrimming = TextTrimming.CharacterEllipsis } });
        AutomationProperties.SetName(AddressPill, (secure ? "Secure connection to " : "Not secure: ") + HostText.Text);
    }

    private async void OnResponseReceived(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (_completed || args.Response.StatusCode != 200) return;
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var request) || request.Scheme != Uri.UriSchemeHttps) return;
        var idx = request.AbsolutePath.IndexOf("/Resources/List", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        var storeWeb = request.GetLeftPart(UriPartial.Authority) + request.AbsolutePath[..(idx + 1)];
        if (!await ConfirmStoreHostAsync(request.Host) || _completed) return;
        Log.Info($"Discovered store at {storeWeb}");
        DonePanel.Visibility = Visibility.Visible;

        try
        {
            var cookies = await sender.CookieManager.GetCookiesAsync(storeWeb);
            var list = new List<Cookie>();
            foreach (var c in cookies)
            {
                try
                {
                    list.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain)
                    {
                        Secure = c.IsSecure,
                        HttpOnly = c.IsHttpOnly,
                        Expires = c.IsSession ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).LocalDateTime,
                    });
                }
                catch { /* skip malformed */ }
            }
            Complete(new SignInResult(storeWeb, list));
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read cookies", ex);
            DonePanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Any page in this window can request a URL like a store's, and the store chosen here decides which launch
    /// files Citrium runs. A store can legitimately sit on another host, so an unexpected one is the user's call.
    /// </summary>
    private async Task<bool> ConfirmStoreHostAsync(string host)
    {
        var entry = new Uri(_account.EntryUrl).Host;
        var known = _account.StoreWebUrl is { } url ? new Uri(url).Host : entry;
        if (host.Equals(entry, StringComparison.OrdinalIgnoreCase) || host.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;
        if (!_askedHosts.Add(host)) return false;

        var dialog = new ContentDialog
        {
            Title = "Use this store?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Sign-in finished at {host}, but this account was added as {entry}. Only continue if your organization uses this address.",
            },
            PrimaryButtonText = "Use this store",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void Complete(SignInResult? result)
    {
        if (_completed) return;
        _completed = true;
        _tcs.TrySetResult(result);
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();
}
