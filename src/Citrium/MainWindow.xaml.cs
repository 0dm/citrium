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
        UpdateTray();
        AppWindow.Closing += OnClosing;
    }

    private readonly TrayIcon _tray;
    private bool _quitting;

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
        _tray.Dispose();
        Close();
        Application.Current.Exit();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        AccountsPage.PromptAddOnLoad = _state.Accounts.Count == 0 && !_settingsRequested;
        Nav.SelectedItem = _settingsRequested ? Nav.SettingsItem : _state.Accounts.Count == 0 ? AccountsItem : HomeItem;

        if (_state.Accounts.Count > 0 && _state.Settings.RefreshOnStartup)
        {
            await _state.RefreshAllAsync(interactive: false);
        }
    }

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
    }

    private async void ShowToast(Notification n)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast.Title = n.Title;
        Toast.Message = n.Message;
        Toast.Severity = n.Severity;
        Toast.IsOpen = true;
        try
        {
            await Task.Delay(n.Severity == InfoBarSeverity.Error ? 12000 : 5000, cts.Token);
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
}
