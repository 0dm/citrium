using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Citrium.Models;
using Citrium.Services;
using Citrium.Viewer;
using Citrium.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Citrium;

public sealed partial class MainWindow : Window
{
    private readonly AppState _state = AppState.Current;
    private CancellationTokenSource? _toastCts;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Citrium.ico"));
        WindowHelper.ResizeCentered(this, 1180, 780);
        WindowHelper.ApplyTheme(this, Root);
        if (AppWindow.Presenter is OverlappedPresenter p) { p.PreferredMinimumWidth = 640; p.PreferredMinimumHeight = 480; }

        Root.Loaded += OnLoaded;

        _state.Notified += (_, n) => ShowToast(n);
        _state.Sessions.Sessions.CollectionChanged += OnSessionsChanged;
        _state.BrandingChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateLogo);
        Root.ActualThemeChanged += (_, _) => UpdateLogo();
        UpdateLogo();

        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Citrium.ico"));
        _tray.OpenRequested += () => DispatcherQueue.TryEnqueue(BringToFront);
        _tray.QuitRequested += () => DispatcherQueue.TryEnqueue(Quit);
        _tray.MenuItems = BuildTrayMenu;
        UpdateTray();
        UpdateTrayTip();
        AppWindow.Closing += OnClosing;

        _state.Sessions.Dropped += OnSessionDropped;
        _lockWatcher = new LockWatcher(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _lockWatcher.Locked += OnWindowsLocked;
        _lockWatcher.Unlocked += OnWindowsUnlocked;
    }

    private readonly TrayIcon _tray;
    private readonly LockWatcher _lockWatcher;
    private List<ViewerInfo> _awayOnLock = [];
    private bool _quitting;

    private bool IsInFront => AppWindow.IsVisible && GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>In Citrium's own notification bar, and in a Windows notification if Citrium isn't what they're looking at.</summary>
    private void Alert(string title, string message, InfoBarSeverity severity, string actionLabel, Action action)
    {
        _state.Notify(title, message, severity, actionLabel, action);
        if (!IsInFront) _tray.Notify(title, message, action);
    }

    /// <summary>Opens a link. Citrium comes forward if it can't, since that's where the reason and any sign-in are.</summary>
    internal async void OpenLink(CitriumLink link)
    {
        if (!await _state.OpenLinkAsync(link)) BringToFront();
    }

    private void OnSessionDropped(HdxSession session)
    {
        if (session.Viewer is not { AccountId: { } account, ResourceId: { } resource } viewer) return;
        if (GetForegroundWindow() == new IntPtr(viewer.WindowHandle)) return; // its own Reconnect button is right in front of them
        Alert($"{session.Name} slipped away", "Its connection ended. Click to bring it back.", InfoBarSeverity.Warning, "Reconnect",
            () => OpenLink(new CitriumLink(account, resource, viewer.WasFullScreen)));
    }

    private void OnWindowsLocked()
    {
        if (!_state.Settings.DisconnectOnLock) return;
        var viewers = ViewerRegistry.ReadAll().Where(v => !v.Ended).ToList();
        _awayOnLock = viewers.Where(v => v.AccountId is not null && v.ResourceId is not null).ToList();
        // One by one: the "disconnect all" signal stays set, and would end desktops opened after unlocking too.
        foreach (var viewer in viewers) ViewerRegistry.RequestDisconnect(viewer.ViewerPid);
        Log.Info($"Windows locked: disconnected {viewers.Count} desktop(s)");
    }

    private void OnWindowsUnlocked()
    {
        var away = _awayOnLock;
        _awayOnLock = [];
        if (away.Count == 0) return;
        var message = away.Count == 1 ? $"{away[0].Title} is waiting. Click to reconnect." : $"{away.Count} desktops are waiting. Click to reconnect.";
        Alert("Welcome back", message, InfoBarSeverity.Informational, "Reconnect", () =>
        {
            foreach (var viewer in away) OpenLink(new CitriumLink(viewer.AccountId!, viewer.ResourceId!));
        });
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var items = new List<TrayMenuItem>();
        var sessions = _state.Sessions.Sessions;
        if (sessions.Count > 0) items.Add(new TrayMenuItem("Running"));
        foreach (var session in sessions)
        {
            if (session.Viewer is { Ended: true, AccountId: { } account, ResourceId: { } resource })
            {
                items.Add(new TrayMenuItem($"{session.Name} (reconnect)", () => OpenLink(new CitriumLink(account, resource))));
            }
            else if (session.Viewer is { } viewer)
            {
                items.Add(new TrayMenuItem(session.Name, Children:
                [
                    new TrayMenuItem("Show", () => SessionMonitor.BringToFront(session)),
                    new TrayMenuItem("Picture in picture", () => ViewerRegistry.RequestPictureInPicture(viewer.ViewerPid)),
                ]));
            }
            else
            {
                items.Add(new TrayMenuItem(session.Name, () => SessionMonitor.BringToFront(session)));
            }
        }

        // Something already running has its row above, with more to offer than opening it again.
        var favorites = _state.Resources
            .Where(r => r.IsFavorite && !sessions.Any(s => s.Name == r.Name && s.AccountName == r.AccountName))
            .Take(MaxTrayFavorites)
            .ToList();
        if (favorites.Count > 0) items.Add(new TrayMenuItem("Favorites"));
        foreach (var favorite in favorites) items.Add(new TrayMenuItem(favorite.Name, () => _ = _state.LaunchAsync(favorite)));
        return items;
    }

    private const int MaxTrayFavorites = 10;

    private void UpdateTrayTip()
    {
        var n = _state.Sessions.Sessions.Count;
        _tray.SetTip(n == 0 ? "Citrium · All quiet" : $"Citrium · {n} session{(n == 1 ? "" : "s")} running");
    }

    public void UpdateTray()
    {
        if (_state.Settings.MinimizeToTray) _tray.Show();
        else _tray.Hide();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitting || !_state.Settings.MinimizeToTray)
        {
            // Citrium is really exiting: desktop viewers (and their sessions) go with it.
            ViewerRegistry.RequestDisconnectAll();
            _lockWatcher.Dispose();
            _tray.Dispose();
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
        if (!_state.Settings.TrayHintShown)
        {
            _state.Settings.TrayHintShown = true;
            _state.SaveSettings();
            _tray.Notify("Citrium is still running",
                "It's in the notification area. Turn this off in Settings, or right-click the icon and select Quit.");
        }
    }

    public void Quit()
    {
        _quitting = true;
        ViewerRegistry.RequestDisconnectAll();
        _lockWatcher.Dispose();
        _tray.Dispose();
        Close();
        Application.Current.Exit();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        AccountsPage.PromptAddOnLoad = _state.Accounts.Count == 0 && !_settingsRequested;
        Nav.SelectedItem = _settingsRequested ? Nav.SettingsItem : _state.Accounts.Count == 0 ? AccountsItem : HomeItem;

        var hasAutoOpen = CitriumLink.TryParse(_state.Settings.AutoOpen, out var autoOpen);
        if (_state.Accounts.Count > 0 && (_state.Settings.RefreshOnStartup || hasAutoOpen))
        {
            await _state.RefreshAllAsync(interactive: false);
        }

        // Not through OpenLink: a sign-in window shouldn't greet someone at startup for this.
        if (hasAutoOpen && _state.Resources.FirstOrDefault(r => r.AccountId == autoOpen.AccountId && r.Id == autoOpen.ResourceId) is { } resource)
        {
            await _state.LaunchAsync(resource);
        }

        UpdateService.DeleteDownload();
        _state.Sessions.Sessions.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(FinishUpdate);
        await CheckForUpdatesInBackgroundAsync();
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        var settings = _state.Settings;
        if (!settings.CheckForUpdates) return;
        if (settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < UpdateCheckInterval) return;

        if (await _state.CheckForUpdatesAsync() is not { } release) return;
        var message = $"Version {release.Version} is out. You have {UpdateService.Current}.";
        if (UpdateService.RunsFromInstall)
        {
            Alert("A fresher Citrium is ready", message, InfoBarSeverity.Informational, "Open Settings", () => DispatcherQueue.TryEnqueue(() =>
            {
                BringToFront();
                ShowSettings();
            }));
        }
        else OfferRelease("A fresher Citrium is ready", message, InfoBarSeverity.Informational);
    }

    private void OfferRelease(string title, string message, InfoBarSeverity severity) =>
        Alert(title, message, severity, "View release",
            () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateService.LatestReleasePage) { UseShellExecute = true })?.Dispose());

    /// <summary>
    /// Only ever from the Download &amp; install link in Settings. Downloads the update, then closes Citrium to install it
    /// and reopen, as soon as no session would go with it.
    /// </summary>
    public async Task InstallUpdateAsync()
    {
        if (_state.UpdateProgress != UpdateProgress.None) return;

        // A version found before a restart comes back from settings, without the release it belongs to.
        var release = _state.LatestRelease ?? await _state.CheckForUpdatesAsync();
        if (release?.Installer is not { } installer)
        {
            OfferRelease("Couldn't update Citrium", "Get the new version from its release page instead.", InfoBarSeverity.Warning);
            return;
        }

        _state.UpdateProgress = UpdateProgress.Downloading;
        _updateInstaller = await UpdateService.DownloadAsync(installer);
        if (_updateInstaller is null)
        {
            _state.UpdateProgress = UpdateProgress.None;
            OfferRelease("Couldn't download the update", "Get the new version from its release page instead.", InfoBarSeverity.Warning);
            return;
        }

        _state.UpdateProgress = UpdateProgress.WaitingForSessions;
        _state.Sessions.Poll();
        FinishUpdate();
    }

    /// <summary>Quitting disconnects every session, so this waits until there are none.</summary>
    private void FinishUpdate()
    {
        if (_updateInstaller is not { } installer || _state.Sessions.Sessions.Count > 0) return;
        _updateInstaller = null;

        if (UpdateService.StartInstall(installer, AppWindow.IsVisible ? null : StartupService.BackgroundArgument))
        {
            Quit();
            return;
        }
        _state.UpdateProgress = UpdateProgress.None;
        OfferRelease("Couldn't update Citrium", "Get the new version from its release page instead.", InfoBarSeverity.Warning);
    }

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(1);
    private string? _updateInstaller;

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        ContentFrame.Tag = e.Parameter;

        object? match = e.SourcePageType == typeof(SettingsPage) ? Nav.SettingsItem
            : e.SourcePageType == typeof(SessionsPage) ? SessionsItem
            : e.SourcePageType == typeof(AccountsPage) ? AccountsItem
            : e.Parameter switch
            {
                ResourcesPage.Mode m => Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == m.ToString().ToLowerInvariant()),
                _ => null,
            };
        if (!ReferenceEquals(Nav.SelectedItem, match)) Nav.SelectedItem = match;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            Navigate(typeof(SettingsPage), null);
            return;
        }
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        switch (tag)
        {
            case "home": Navigate(typeof(ResourcesPage), ResourcesPage.Mode.Home); break;
            case "apps": Navigate(typeof(ResourcesPage), ResourcesPage.Mode.Apps); break;
            case "desktops": Navigate(typeof(ResourcesPage), ResourcesPage.Mode.Desktops); break;
            case "sessions": Navigate(typeof(SessionsPage), null); break;
            case "accounts": Navigate(typeof(AccountsPage), null); break;
        }
    }

    private void Navigate(Type page, object? parameter)
    {
        if (ContentFrame.Content?.GetType() == page && Equals(ContentFrame.Tag, parameter)) return;
        ContentFrame.Tag = parameter;
        ContentFrame.Navigate(page, parameter, new EntranceNavigationTransitionInfo());
    }

    public async void UpdateLogo()
    {
        var logo = _state.Settings.ShowOrganizationLogo ? _state.OrganizationLogo : null;
        if (logo is null) { OrgLogo.Visibility = Visibility.Collapsed; return; }
        var path = Root.ActualTheme == ElementTheme.Dark ? logo.DarkPath : logo.LightPath;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            stream.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bmp.SetSourceAsync(stream);
            OrgLogo.Source = bmp;
            OrgLogo.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't show the organization logo", ex);
            OrgLogo.Visibility = Visibility.Collapsed;
        }
    }

    public void ShowAccounts() => Nav.SelectedItem = AccountsItem;
    private bool _loaded, _settingsRequested;

    public void ShowSettings()
    {
        if (_loaded) Nav.SelectedItem = Nav.SettingsItem;
        else _settingsRequested = true;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var q = sender.Text.Trim();
        sender.ItemsSource = q.Length == 0
            ? null
            : _state.Resources
                .Where(r => r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Folder.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(r => r.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
                .ThenBy(r => r.Name)
                .Take(8)
                .ToList();
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is StoreResource r) sender.Text = r.Name;
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is StoreResource r)
        {
            sender.Text = "";
            await _state.LaunchAsync(r);
            return;
        }
        var q = args.QueryText.Trim();
        if (q.Length == 0) return;
        Navigate(typeof(ResourcesPage), "search:" + q);
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var n = _state.Sessions.Sessions.Count;
        SessionsBadge.Value = n;
        SessionsBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTrayTip();
    }

    private async void ShowToast(Notification n)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast.Title = n.Title;
        Toast.Message = n.Message;
        Toast.Severity = n.Severity;
        Toast.ActionButton = null;
        if (n is { ActionLabel: { } label, Action: { } action })
        {
            var button = new Button { Content = label };
            button.Click += (_, _) =>
            {
                Toast.IsOpen = false;
                action();
            };
            Toast.ActionButton = button;
        }
        Toast.IsOpen = true;
        try
        {
            await Task.Delay(n.Severity == InfoBarSeverity.Error || n.ActionLabel is not null ? 12000 : 5000, cts.Token);
            Toast.IsOpen = false;
        }
        catch (TaskCanceledException) { }
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        AppWindow.Show();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
