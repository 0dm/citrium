using System.Diagnostics;
using System.Reflection;
using Citrium.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Citrium.Views;

public sealed partial class SettingsPage : Page
{
    private readonly AppState _state = AppState.Current;
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var s = _state.Settings;
        ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Theme) ?? ThemeBox.Items[0];
        BackdropBox.SelectedItem = BackdropBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Backdrop) ?? BackdropBox.Items[0];
        RefreshToggle.IsOn = s.RefreshOnStartup;
        TrayToggle.IsOn = s.MinimizeToTray;
        LogoToggle.IsOn = s.ShowOrganizationLogo;
        KeepIcaToggle.IsOn = s.KeepIcaFiles;
        StartupToggle.IsOn = StartupService.IsEnabled;
        LockToggle.IsOn = s.DisconnectOnLock;
        UpdatesToggle.IsOn = s.CheckForUpdates;
        FillAutoOpen();
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        AboutCard.Description = $"Version {version} · A native Windows client for Citrix stores.";
        UpdateEngine();
        UpdateViewer();
        ShowUpdateStatus();
        _state.UpdateStatusChanged += OnUpdateStatusChanged;
        Unloaded += (_, _) => _state.UpdateStatusChanged -= OnUpdateStatusChanged;
        _loading = false;
    }

    private void FillAutoOpen()
    {
        var saved = _state.Settings.AutoOpen ?? "";
        AutoOpenBox.Items.Add(new ComboBoxItem { Content = "Nothing", Tag = "" });
        foreach (var r in _state.Resources.OrderByDescending(r => r.IsDesktop).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            AutoOpenBox.Items.Add(new ComboBoxItem { Content = r.Name, Tag = AppState.LinkTo(r).ToUri() });

        // Signed out, the store's list is empty, and that shouldn't quietly lose the choice.
        var match = AutoOpenBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == saved);
        if (match is null)
        {
            match = new ComboBoxItem { Content = "Your saved choice", Tag = saved };
            AutoOpenBox.Items.Add(match);
        }
        AutoOpenBox.SelectedItem = match;
    }

    private void AutoOpenBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AutoOpenBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _state.Settings.AutoOpen = tag.Length == 0 ? null : tag;
        _state.SaveSettings();
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            StartupService.SetEnabled(StartupToggle.IsOn);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("Couldn't change starting with Windows", ex);
            _state.Notify("Couldn't change that setting", "Your organization may manage which programs start with Windows.");
        }
    }

    private void LockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.Settings.DisconnectOnLock = LockToggle.IsOn;
        _state.SaveSettings();
    }

    private void UpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.Settings.CheckForUpdates = UpdatesToggle.IsOn;
        _state.SaveSettings();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e) => await _state.CheckForUpdatesAsync();

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateService.RunsFromInstall)
            Process.Start(new ProcessStartInfo(UpdateService.LatestReleasePage) { UseShellExecute = true })?.Dispose();
        else if (App.Current?.MainWindow is { } window)
            await window.InstallUpdateAsync();
    }

    /// <summary>Only until Settings is next opened: this bar is the way to the release.</summary>
    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => UpdateAvailableBar.Visibility = Visibility.Collapsed;

    private void OnUpdateStatusChanged(object? sender, EventArgs e) => ShowUpdateStatus();

    private void ShowUpdateStatus()
    {
        var update = _state.AvailableUpdate;
        var lastChecked = _state.Settings.LastUpdateCheck is { } when
            ? $"Last checked: {UpdateService.DescribeLastCheck(when, DateTimeOffset.Now)}"
            : "Not checked yet";

        var progress = _state.UpdateProgress;
        var busy = _state.CheckingForUpdates || progress == UpdateProgress.Downloading;

        var (title, detail, badge, glyph) =
            progress == UpdateProgress.Downloading ? ($"Downloading Citrium {update}", lastChecked, null, "")
            : progress == UpdateProgress.WaitingForSessions ? ("Update ready", $"Citrium {update} installs when your sessions end. Citrium reopens by itself.", "UpdateBadgeInfo", "\uE74A")
            : _state.CheckingForUpdates ? ("Checking for updates", lastChecked, null, "")
            : _state.UpdateCheckFailed ? ("Couldn't check for updates", "GitHub has no release to compare with, or can't be reached right now.", "UpdateBadgeCaution", "\uE8C9")
            : update is not null ? ("An update is available", lastChecked, "UpdateBadgeInfo", "\uE74A")
            : _state.Settings.LastUpdateCheck is not null ? ("You're up to date", lastChecked, "UpdateBadgeSuccess", "\uE73E")
            : ($"Citrium {UpdateService.Current}", lastChecked, (string?)null, "");

        UpdateTitle.Text = title;
        UpdateDetail.Text = detail;
        UpdateBadge.Visibility = badge is null ? Visibility.Collapsed : Visibility.Visible;
        if (badge is not null)
        {
            UpdateBadgeFill.Fill = (Brush)Resources[badge];
            UpdateBadgeIcon.Glyph = glyph;
        }
        UpdateRing.IsActive = busy;
        UpdateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        UpdateAvailableBar.Visibility = update is null || progress != UpdateProgress.None ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionLink.Content = UpdateService.RunsFromInstall ? "Download & install" : "View release";
        UpdateAvailableText.Text = $"Citrium {update} is available.";
    }

    private void UpdateViewer()
    {
        var available = Viewer.ViewerLauncher.IsAvailable(out var reason);
        ViewerBox.SelectedItem = ViewerBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == (available ? _state.Settings.DesktopViewer : "Citrix")) ?? ViewerBox.Items[0];
        ViewerBox.IsEnabled = available;
        InstallWorkspaceButton.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        ViewerCard.Description = available
            ? "Citrium's viewer keeps its controls in the title bar, and in full screen they appear when you rest the pointer at the edge of the screen. Apps always open in their own windows."
            : reason;
    }

    /// <summary>
    /// Installs Citrix Workspace app (64-bit) with winget (official Citrix installer, hash-verified by winget).
    /// Runs in a visible terminal so the user sees Citrix's licence terms and approves the admin prompt;
    /// Citrium never accepts the licence on their behalf.
    /// </summary>
    private async void InstallWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Install Citrix Workspace app?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Citrium will use Windows Package Manager (winget) to download Citrix Workspace app from Citrix " +
                       "and install it. You'll be asked to accept Citrix's licence " +
                       "and to allow the installer to make changes.\n\nIf your PC is managed by your organization, check with " +
                       "your IT department first. Close any open Citrix sessions before continuing.",
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            Process.Start(new ProcessStartInfo("winget.exe",
                "install --id Citrix.Workspace --exact --architecture x64 --source winget")
            { UseShellExecute = true });
            _state.Notify("Installing Citrix Workspace", "Follow the steps in the installer window, then restart Citrium.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Log.Error("winget launch failed", ex);
            _state.Notify("Couldn't start the installer",
                "Windows Package Manager (winget) isn't available. Download the 64-bit Citrix Workspace app from citrix.com instead.");
        }
    }

    private void ViewerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ViewerBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _state.Settings.DesktopViewer = tag;
        _state.SaveSettings();
    }

    private void UpdateEngine()
    {
        var path = HdxEngine.Locate(_state.Settings.HdxEnginePath);
        EngineStatus.Text = path is null ? "Not found" : $"Version {HdxEngine.Version(path)}";
#if DEMO
        EngineStatus.Text = "Demo machine";
        EnginePathCard.Description = "No Citrix here: a demo build brings its own fake machine.";
#else
        EnginePathCard.Description = path ?? "Install Citrix Workspace app, or point Citrium at wfica32.exe.";
#endif
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _state.Settings.Theme = tag;
        _state.SaveSettings();
        WindowHelper.ApplyThemeToAll();
    }

    private void BackdropBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackdropBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _state.Settings.Backdrop = tag;
        _state.SaveSettings();
        WindowHelper.ApplyThemeToAll();
    }

    private void RefreshToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.Settings.RefreshOnStartup = RefreshToggle.IsOn;
        _state.SaveSettings();
    }

    private void LogoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.Settings.ShowOrganizationLogo = LogoToggle.IsOn;
        _state.SaveSettings();
        App.Current?.MainWindow?.UpdateLogo();
    }

    private void TrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.Settings.MinimizeToTray = TrayToggle.IsOn;
        _state.SaveSettings();
        App.Current?.MainWindow?.UpdateTray();
    }

    private void KeepIcaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _state.Settings.KeepIcaFiles = KeepIcaToggle.IsOn;
        _state.SaveSettings();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.Current!.MainWindow!));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _state.Settings.HdxEnginePath = file.Path;
        _state.SaveSettings();
        UpdateEngine();
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.HdxEnginePath = null;
        _state.SaveSettings();
        UpdateEngine();
    }

    private void ClearIcons_Click(object sender, RoutedEventArgs e)
    {
        IconCache.Clear();
        _state.Notify("Icon cache cleared", "Icons will be downloaded again on the next refresh.", InfoBarSeverity.Success);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
}
