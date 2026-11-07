using System.Runtime.InteropServices;
using Citrium.Models;
using Citrium.Services;
using Citrium.Viewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Citrium.Views;

public sealed record ViewerOptions(
    string IcaFile, string Title, string? Account, bool DeleteIcaFile,
    string? AccountId = null, string? ResourceId = null, bool FullScreen = false)
{
    public static ViewerOptions? TryParse(string[] args)
    {
        var i = Array.IndexOf(args, "--viewer");
        if (i < 0 || i + 1 >= args.Length) return null;
        string? Arg(string name) { var j = Array.IndexOf(args, name); return j >= 0 && j + 1 < args.Length ? args[j + 1] : null; }
        return new ViewerOptions(args[i + 1], Arg("--title") ?? "Citrix desktop", Arg("--account"), args.Contains("--delete-ica"),
            Arg("--account-id"), Arg("--resource-id"), args.Contains("--fullscreen"));
    }
}

/// <summary>
/// Citrium's own session window for Citrix desktops. Citrix's session surface is adopted into a child window
/// over <see cref="SessionArea"/>; everything around it (title bar, controls, status, full screen toolbar) is
/// Citrium's, so the controls are always reachable.
/// </summary>
public sealed partial class ViewerWindow : Window
{
    private readonly ViewerOptions _options;
    private readonly DispatcherQueueTimer _resizeTimer;
    private readonly DispatcherQueueTimer _edgeTimer;
    private readonly DispatcherQueueTimer _adoptTimer;
    private readonly DispatcherQueueTimer _foregroundTimer;
    private readonly DispatcherQueueTimer _placementTimer;
    private readonly SubclassProc _messageHook; // a field: Windows holds the only other reference
    private readonly string _placementKey;
    private WindowPlacement? _placement;
    private DateTime _revealedAt = DateTime.MaxValue;
    private bool _ended, _endedFullScreen, _cloakedAway;
    private AltTabForwarder? _altTab;
    private Microsoft.UI.Xaml.Media.Brush? _statusBackground;
    private bool _adopted;
    private SessionSurfaceHost? _host;
    private FullScreenToolbar? _toolbar;
    private ViewerSettingsPanel? _settingsPanel;
    private PictureInPictureWindow? _pip;
    private readonly ViewerSettings _viewerSettings = SettingsService.LoadViewer();
    private bool _sessionVisible, _exitRequested, _disconnecting, _hidden, _fullScreen;
    private bool _restoreFullScreen;
    private DateTime _toolbarHideAt = DateTime.MaxValue;
    private CancellationTokenSource? _watchCts;
    private EventWaitHandle? _disconnectMe, _disconnectAll, _showMe, _pictureInPictureMe;

    public ViewerWindow(ViewerOptions options)
    {
        _options = options;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Title = string.IsNullOrEmpty(options.Account) ? options.Title : $"{options.Title} – {options.Account}";
        AppTitleBar.Title = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Citrium.ico"));
        _placementKey = $"{options.Account}|{options.Title}";
        _placement = PlacementStore.Load().Desktops.GetValueOrDefault(_placementKey);
        WindowHelper.ResizeCentered(this, DefaultWidthDip, DefaultHeightDip);
        WindowHelper.ApplyTheme(this, Root);
        StatusTitle.Text = $"Connecting to {options.Title}…";

        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(700);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => ResizeRemoteDesktop();

        _adoptTimer = DispatcherQueue.CreateTimer();
        _adoptTimer.Interval = TimeSpan.FromMilliseconds(150);
        _adoptTimer.Tick += (_, _) => AdoptTick();

        // Citrix's hidden frame takes the foreground on connect, and that's where typing would go.
        _foregroundTimer = DispatcherQueue.CreateTimer();
        _foregroundTimer.Interval = TimeSpan.FromMilliseconds(100);
        _foregroundTimer.Tick += (_, _) => ReclaimKeyboard();

        _edgeTimer = DispatcherQueue.CreateTimer();
        _edgeTimer.Interval = TimeSpan.FromMilliseconds(120);
        _edgeTimer.Tick += (_, _) => TrackToolbarEdge();

        // Saved after the window has been still for a moment, not on every step of a drag.
        _placementTimer = DispatcherQueue.CreateTimer();
        _placementTimer.Interval = TimeSpan.FromMilliseconds(800);
        _placementTimer.IsRepeating = false;
        _placementTimer.Tick += (_, _) => SavePlacement();
        AppWindow.Changed += (_, e) =>
        {
            if (!e.DidPositionChange && !e.DidSizeChange) return;
            _placementTimer.Stop();
            _placementTimer.Start();
        };

        _messageHook = OnWindowMessage;
        SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), _messageHook, 1, 0);

        SessionArea.SizeChanged += (_, _) => { PositionSession(); _resizeTimer.Start(); };
        AppWindow.Closing += OnClosing;
        Activated += OnActivated;
        Root.Loaded += (_, _) =>
        {
            // MoveAndResize in the constructor is kept as the restore rect, but the first show
            // ignores the position. Center again once the window is actually on screen.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PlaceWindow);
            _ = Task.Delay(300).ContinueWith(_ => DispatcherQueue.TryEnqueue(PlaceWindow));
            StartSignals();
            StartSession();
        };
    }

    private void PlaceWindow()
    {
        if (_fullScreen || _hidden || _pip is not null) return;
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not { State: OverlappedPresenterState.Maximized })
        {
            var saved = _placement is { } s ? WindowFit.Fit(new ScreenRect(s.X, s.Y, s.Width, s.Height), WindowHelper.WorkAreas()) : null;
            if (saved is { } rect) WindowHelper.MoveTo(this, rect);
            else WindowHelper.ResizeCentered(this, DefaultWidthDip, DefaultHeightDip);
            if (_placement is { Maximized: true }) presenter?.Maximize();
        }
        var p = AppWindow.Position;
        Log.Info($"Viewer window at {p.X},{p.Y} {AppWindow.Size.Width}x{AppWindow.Size.Height}");
        PositionSession();
    }

    /// <summary>
    /// Keeps what the user did with the window. Adopting the session surface can throw it around, and PlaceWindow
    /// puts that right, so nothing is saved until that's over.
    /// </summary>
    private void SavePlacement()
    {
        if (!_adopted || _ended || _fullScreen || _hidden || _pip is not null) return;
        if (DateTime.UtcNow - _revealedAt < PlacementSettleTime) return;
        if (IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(this))) return;

        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        UpdatePlacement(entry =>
        {
            entry.Maximized = maximized;
            if (maximized) return; // the size to come back to is the one from before maximizing
            (entry.X, entry.Y, entry.Width, entry.Height) = (position.X, position.Y, size.Width, size.Height);
        });
    }

    private void UpdatePlacement(Action<WindowPlacement> change) => PlacementStore.Update(all =>
    {
        var entry = all.Desktops.GetValueOrDefault(_placementKey) ?? new WindowPlacement();
        change(entry);
        all.Desktops[_placementKey] = entry;
        _placement = entry;
    });

    private const int DefaultWidthDip = 1366, DefaultHeightDip = 860;
    private static readonly TimeSpan PlacementSettleTime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AdoptTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReconnectPatience = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SwitchAwayDelay = TimeSpan.FromMilliseconds(700);
    private DateTime _launchedAt;
    private bool _gaveUp;
    private int _deadTicks;

    private void StartSession()
    {
        if (HdxEngine.Locate(AppState.Current.Settings.HdxEnginePath) is null)
        {
            ShowError("Citrix Workspace app isn't installed", "Citrium uses the Citrix HDX engine from Citrix Workspace app to display desktops.");
            return;
        }
        try
        {
            _host = new SessionSurfaceHost(WinRT.Interop.WindowNative.GetWindowHandle(this), _options.Title);
            _host.Show(false);
            PositionSession();
            IcaFileEditor.SetValue(_options.IcaFile, "WFClient", "TransparentKeyPassthrough", _viewerSettings.KeyPassthrough);
            WriteDisplaySettings(_options.IcaFile);
            WriteAudioSettings(_options.IcaFile);
            _host.Launch(_options.IcaFile, AppState.Current.Settings.HdxEnginePath);
            _launchedAt = DateTime.UtcNow;
            PublishRegistration();
            _adoptTimer.Start();
            _foregroundTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Viewer failed to start the session", ex);
            ShowError("Couldn't start the session", ex.Message);
        }
        finally
        {
            if (_options.DeleteIcaFile)
            {
                // The engine reads the file at startup; the logon ticket inside is single-use.
                _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => { try { File.Delete(_options.IcaFile); } catch { } });
            }
        }
    }

    private void WriteDisplaySettings(string icaFile)
    {
        switch (_viewerSettings.DpiScaling)
        {
            case "Native":
                IcaFileEditor.SetValue(icaFile, "WFClient", "Enable_DPI", "0");
                IcaFileEditor.RemoveValue(icaFile, "WFClient", "DpiAware");
                break;
            case "System":
                // DpiAware=2 is Citrix's DPI-unaware mode: Windows scales the session instead.
                IcaFileEditor.SetValue(icaFile, "WFClient", "DpiAware", "2");
                IcaFileEditor.RemoveValue(icaFile, "WFClient", "Enable_DPI");
                break;
            default: // "Yes": scale the session to match this screen's DPI.
                IcaFileEditor.SetValue(icaFile, "WFClient", "Enable_DPI", "1");
                IcaFileEditor.RemoveValue(icaFile, "WFClient", "DpiAware");
                break;
        }

        IcaFileEditor.SetValue(icaFile, "WFClient", "EnableH265", _viewerSettings.EnableH265 ? "1" : "0");

        // 0 means automatic: leave the key out so the Thinwire driver sizes the cache itself.
        if (_viewerSettings.Tw2CachePower is >= 19 and <= 25)
            IcaFileEditor.SetValue(icaFile, "Thinwire3.0", "Tw2CachePower",
                _viewerSettings.Tw2CachePower.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else
            IcaFileEditor.RemoveValue(icaFile, "Thinwire3.0", "Tw2CachePower");
    }

    /// <summary>
    /// Writes the viewer's audio choices into the launch file. "Default" and an allowed microphone write nothing,
    /// so the store's values stand, including a microphone it has turned off.
    /// </summary>
    private void WriteAudioSettings(string icaFile)
    {
        if (_viewerSettings.AudioQuality == "Off")
        {
            IcaFileEditor.SetSessionValue(icaFile, "ClientAudio", "Off");
        }
        else if (_viewerSettings.AudioQuality switch { "High" => "0", "Medium" => "1", "Low" => "2", _ => null } is { } limit)
        {
            // Quality only: sound the store turned off stays off.
            IcaFileEditor.SetSessionValue(icaFile, "AudioBandwidthLimit", limit);
        }

        if (!_viewerSettings.AllowMicrophone) IcaFileEditor.SetSessionValue(icaFile, "EnableAudioInput", "false");
    }

    private void AdoptTick()
    {
        if (_host is null) { _adoptTimer.Stop(); return; }
        try
        {
            if (_viewerSettings.HideCitrixToasts && DateTime.UtcNow - _launchedAt < TimeSpan.FromSeconds(30)) _host.HideConnectionToasts();

            if (_host.EnsureAdopted())
            {
                _deadTicks = 0;
                if (!_adopted) RevealSession();
                return;
            }

            if (_adopted)
            {
                // The surface went away. Wait a few ticks so a recreate mid-resize isn't a disconnect, then stay open and say so.
                if (!_host.IsSessionAlive && ++_deadTicks >= 4)
                {
                    Log.Info($"Viewer[{_options.Title}] session ended (engine={_host.Engine?.Id} surface={_host.SurfaceProcessId})");
                    OnSessionEnded();
                }
                return;
            }

            if (!_host.IsSessionAlive && DateTime.UtcNow - _launchedAt > TimeSpan.FromSeconds(3))
            {
                // wfica32 exited before a session appeared; Citrix shows its own error dialog for the reason.
                // A hidden viewer has nowhere to show that, so it leaves the session list instead.
                if (_hidden) { ExitViewer(); return; }
                ShowError($"Couldn't connect to {_options.Title}", "Citrix couldn't start the session. Try again from Citrium.");
                _adoptTimer.Stop();
                return;
            }

            if (!_gaveUp && DateTime.UtcNow - _launchedAt > AdoptTimeout)
            {
                _gaveUp = true;
                _host.GiveUpAndShowCitrixViewer();
                if (_hidden) { ExitViewer(); return; }
                ShowInfo($"{_options.Title} opened in Citrix Desktop Viewer", "Citrium couldn't take over this session's window.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Session watchdog failed", ex);
        }
    }

    /// <summary>
    /// The viewer stays, even hidden: it's what Sessions lists as disconnected, and it holds what Reconnect needs.
    /// Telling the user is the main window's job, because a hidden viewer has nowhere to do it.
    /// </summary>
    private void OnSessionEnded()
    {
        _ended = true;
        _endedFullScreen = _fullScreen || _restoreFullScreen;
        _sessionVisible = false;
        _adoptTimer.Stop();
        _foregroundTimer.Stop();
        if (CanReconnect) ShowError($"{_options.Title} slipped away", "The connection ended. Your desktop may still be running, so reconnecting should put you back where you were.");
        else ShowError("The session ended", "The desktop disconnected. Launch it again from Citrium to reconnect.");
        StatusReconnect.Visibility = CanReconnect ? Visibility.Visible : Visibility.Collapsed;
        PublishRegistration();
    }

    private bool CanReconnect => _options.AccountId is not null && _options.ResourceId is not null;

    /// <summary>
    /// Asks the main app to open this desktop again, by the same link a shortcut uses. It needs a new logon ticket,
    /// which only the main app can get, and it closes this window once the new one is on its way.
    /// </summary>
    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        StatusReconnect.Visibility = Visibility.Collapsed;
        StatusClose.Visibility = Visibility.Collapsed;
        StatusIcon.Visibility = Visibility.Collapsed;
        StatusRing.Visibility = Visibility.Visible;
        StatusRing.IsActive = true;
        StatusTitle.Text = "Fetching a fresh ticket…";
        StatusText.Text = "";

        try
        {
            var link = new CitriumLink(_options.AccountId!, _options.ResourceId!, _endedFullScreen);
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(link.ToUri());
            System.Diagnostics.Process.Start(start)?.Dispose();
            await Task.Delay(ReconnectPatience);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't ask Citrium to reconnect", ex);
        }

        if (_exitRequested || _disconnecting) return;
        ShowError($"{_options.Title} is still away", "Citrium couldn't reopen it. It may be waiting for you to sign in.");
        StatusReconnect.Visibility = Visibility.Visible;
    }

    private void ReclaimKeyboard()
    {
        // Never pull a hidden or minimized viewer forward: only reclaim what Citrix's hidden frame took.
        if (_host is null || _hidden || _gaveUp || _pip is not null) return;
        if (IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(this))) return;
        WatchForSwitchAway();
        if (_pip is not null) return;
        _host.ReclaimForegroundFromCitrix();
        _host.KeepKeyboardOnSession();
    }

    private bool _heldForeground;
    private DateTime _awaySince = DateTime.MaxValue;
    private IntPtr _lastForeground;
    private bool _lastForegroundIsSession;

    /// <summary>Shrinks to picture in picture once another program has really taken over.</summary>
    private void WatchForSwitchAway()
    {
        if (_viewerSettings.AutoPictureInPicture != "SwitchAway" || !CanEnterPictureInPicture) return;
        var foreground = GetForegroundWindow();
        if (foreground == WinRT.Interop.WindowNative.GetWindowHandle(this))
        {
            _heldForeground = true;
            _awaySince = DateTime.MaxValue;
            return;
        }
        if (!_heldForeground || foreground == IntPtr.Zero) return;
        if (BelongsToSession(foreground))
        {
            _awaySince = DateTime.MaxValue;
            return;
        }

        // A moment's grace, so a window that flashes up on its way somewhere doesn't shrink the desktop.
        if (_awaySince == DateTime.MaxValue) _awaySince = DateTime.UtcNow;
        else if (DateTime.UtcNow - _awaySince >= SwitchAwayDelay)
        {
            _heldForeground = false;
            _awaySince = DateTime.MaxValue;
            SetPictureInPicture(true);
        }
    }

    /// <summary>This viewer's toolbars and panels, and Citrix's own windows, don't count as leaving.</summary>
    private bool BelongsToSession(IntPtr window)
    {
        if (window == _lastForeground) return _lastForegroundIsSession;
        _lastForeground = window;
        _lastForegroundIsSession = false;
        GetWindowThreadProcessId(window, out var pid);
        if (pid == (uint)Environment.ProcessId) return _lastForegroundIsSession = true;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            _lastForegroundIsSession = name.StartsWith("Citrix", StringComparison.OrdinalIgnoreCase)
                || name.Equals(HdxEngine.ProcessName, StringComparison.OrdinalIgnoreCase)
                || name.Equals("CDViewer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Receiver", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { /* already gone */ }
        return _lastForegroundIsSession;
    }

    private bool SessionOwnsAltTab()
    {
        if (_host is null || _hidden || !_adopted || _ended) return false;
        var passthrough = _viewerSettings.KeyPassthrough;
        if (passthrough == "Local" || (passthrough != "Remote" && !_fullScreen)) return false;
        var fg = GetForegroundWindow();
        return fg == WinRT.Interop.WindowNative.GetWindowHandle(this)
            || (_toolbar is not null && fg == WinRT.Interop.WindowNative.GetWindowHandle(_toolbar));
    }

    private void RevealSession()
    {
        if (_host is null) return;
        _adopted = true;
        _altTab ??= new AltTabForwarder(SessionOwnsAltTab,
            reverse => DispatcherQueue.TryEnqueue(() => _ = _host?.SendAltTabAsync(reverse)));
        _sessionVisible = true;
        _revealedAt = DateTime.UtcNow;
        PictureInPictureButton.IsEnabled = true;
        SendToCitriumButton.IsEnabled = true;
        _adoptTimer.Interval = TimeSpan.FromMilliseconds(400);
        StatusPanel.Visibility = Visibility.Collapsed;
        // Behind the remote desktop: black, so any edge the session doesn't cover reads as letterboxing.
        _statusBackground ??= SessionArea.Background;
        SessionArea.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black);
        PositionSession();
        _host.Show(true);
        PublishRegistration();
        Log.Info($"Viewer[{_options.Title}] showing session after {(DateTime.UtcNow - _launchedAt).TotalSeconds:F1}s");
        if (_hidden)
        {
            // Closed while it was still connecting. Remember full screen for when Show opens it again.
            if (_viewerSettings.StartFullScreen || _options.FullScreen) _restoreFullScreen = true;
            return;
        }
        Activate();
        _host.Focus();
        if (_viewerSettings.StartFullScreen || _options.FullScreen) SetFullScreen(true, onRememberedScreen: true);
        else
        {
            // Adopting the session surface can shove the frame back to the corner, so place it again.
            PlaceWindow();
            _ = Task.Delay(500).ContinueWith(_ => DispatcherQueue.TryEnqueue(PlaceWindow));
        }
    }

    private void ShowError(string title, string message) => ShowStatus(title, message, error: true);

    private void ShowInfo(string title, string message) => ShowStatus(title, message, error: false);

    private void ShowStatus(string title, string message, bool error)
    {
        PictureInPictureButton.IsEnabled = false;
        SendToCitriumButton.IsEnabled = false;
        SetPictureInPicture(false);
        _host?.Show(false);
        // Status text follows the theme, so it needs the theme background back, not the black behind the session.
        if (_statusBackground is not null) SessionArea.Background = _statusBackground;
        StatusPanel.Visibility = Visibility.Visible;
        StatusRing.IsActive = false;
        StatusRing.Visibility = Visibility.Collapsed;
        StatusIcon.Glyph = error ? "" : "";
        StatusIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[error ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush"];
        StatusIcon.Visibility = Visibility.Visible;
        StatusTitle.Text = title;
        StatusText.Text = message;
        StatusClose.Visibility = Visibility.Visible;
        StatusReconnect.Visibility = Visibility.Collapsed;
        if (_fullScreen) SetFullScreen(false);
    }

    private (int W, int H) SessionAreaPixels()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        return ((int)Math.Round(SessionArea.ActualWidth * scale), (int)Math.Round(SessionArea.ActualHeight * scale));
    }

    private void PositionSession()
    {
        if (_host is null || Root.XamlRoot is null) return;
        var scale = Root.XamlRoot.RasterizationScale;
        var origin = SessionArea.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var (w, h) = SessionAreaPixels();
        _host.SetBounds((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale), w, h);
        _pip?.Refresh();
    }

    private void ResizeRemoteDesktop()
    {
        if (_host is null || !_adopted) return;
        PositionSession();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Clicking our title bar activates the frame; hand keyboard focus back to the session.
        if (args.WindowActivationState != WindowActivationState.Deactivated && _sessionVisible) _host?.Focus();
    }

    private void SetFullScreen(bool on, bool onRememberedScreen = false)
    {
        if (_fullScreen == on) return;
        _fullScreen = on;
        if (on && onRememberedScreen) MoveToRememberedScreen();
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        AppTitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        if (on)
        {
            _toolbar ??= CreateToolbar();
            _edgeTimer.Start();
            ShowToolbar(_toolbar.IsPinned ? null : TimeSpan.FromSeconds(2.5));
        }
        else
        {
            _edgeTimer.Stop();
            _toolbar?.AppWindow.Hide();
            _settingsPanel?.Hide();
        }

        // Re-place the session surface once the new layout has settled. Leaving full screen often restores
        // the exact previous size, so SizeChanged doesn't fire, and the surface would keep covering the title bar.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PositionSession);
        _ = Task.Delay(250).ContinueWith(_ => DispatcherQueue.TryEnqueue(PositionSession));
        _host?.Focus();
    }

    /// <summary>Only for a desktop opening by itself: pressing Full screen means this screen, wherever it was last time.</summary>
    private void MoveToRememberedScreen()
    {
        if (_placement is not { FullScreenX: { } x, FullScreenY: { } y }) return;
        var target = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(x, y), DisplayAreaFallback.None);
        if (target is null || target.DisplayId.Value == CurrentDisplay.DisplayId.Value) return;
        AppWindow.Move(new Windows.Graphics.PointInt32(target.WorkArea.X + ScreenMoveInset, target.WorkArea.Y + ScreenMoveInset));
    }

    private DisplayArea CurrentDisplay => DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);

    private void MoveToNextScreen()
    {
        var displays = WindowHelper.Displays();
        if (displays.Count < 2 || !_fullScreen) return;
        var current = displays.FindIndex(d => d.DisplayId.Value == CurrentDisplay.DisplayId.Value);
        var next = displays[(current + 1) % displays.Count];

        // Full screen sticks to its screen, so it has to let go for the move.
        _toolbar?.AppWindow.Hide();
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        AppWindow.Move(new Windows.Graphics.PointInt32(next.WorkArea.X + ScreenMoveInset, next.WorkArea.Y + ScreenMoveInset));
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        var bounds = next.OuterBounds;
        UpdatePlacement(entry =>
        {
            entry.FullScreenX = bounds.X + bounds.Width / 2;
            entry.FullScreenY = bounds.Y + bounds.Height / 2;
        });
        PositionSession();
        _ = Task.Delay(250).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            PositionSession();
            if (_fullScreen) ShowToolbar(_toolbar is { IsPinned: true } ? null : TimeSpan.FromSeconds(2.5));
        }));
        _host?.Focus();
    }

    private const int ScreenMoveInset = 32;

    private FullScreenToolbar CreateToolbar()
    {
        var dock = Enum.TryParse<ToolbarDock>(_viewerSettings.ToolbarDock, out var saved) ? saved : ToolbarDock.Top;
        var t = new FullScreenToolbar(_options.Title, _viewerSettings.PinToolbar, dock, _viewerSettings.ToolbarX, _viewerSettings.ToolbarY,
            multipleScreens: WindowHelper.Displays().Count > 1);
        t.SettingsRequested += () => ToggleSettingsPanel(t.SettingsPanelAnchor);
        t.PlacementChanged += SaveToolbarPlacement;
        t.CtrlAltDelRequested += () => _ = _host?.SendCtrlAltDelAsync();
        t.MinimizeRequested += Minimize;
        t.NextScreenRequested += MoveToNextScreen;
        t.ExitFullScreenRequested += () => SetFullScreen(false);
        t.PictureInPictureRequested += () => SetPictureInPicture(true);
        t.SendToCitriumRequested += HideToSession;
        t.DisconnectRequested += () => _ = DisconnectAndExitAsync();
        t.PinChanged += SetToolbarPinned;
        return t;
    }

    private void SaveToolbarPlacement(ToolbarDock dock, double x, double y)
    {
        _viewerSettings.ToolbarDock = dock.ToString();
        _viewerSettings.ToolbarX = x;
        _viewerSettings.ToolbarY = y;
        SettingsService.SaveViewer(_viewerSettings);
    }

    private void Minimize()
    {
        if (ShrinksInsteadOfMinimizing)
        {
            SetPictureInPicture(true);
            return;
        }
        _toolbar?.AppWindow.Hide();
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), 6 /* SW_MINIMIZE */);
    }

    private void SetToolbarPinned(bool pinned)
    {
        if (_viewerSettings.PinToolbar != pinned)
        {
            _viewerSettings.PinToolbar = pinned;
            SettingsService.SaveViewer(_viewerSettings);
        }
        ApplyViewerSettings(_viewerSettings);
    }

    /// <summary>Applies settings that take effect immediately (the rest apply on the next connection).</summary>
    private void ApplyViewerSettings(ViewerSettings s)
    {
        _toolbar?.SetPinned(s.PinToolbar);
        _settingsPanel?.SetPinned(s.PinToolbar);
        if (s.PinToolbar && _fullScreen) ShowToolbar();
    }

    // The small window shows a live thumbnail of this one, so this window has to keep drawing at its full size:
    // resizing it would change the session's resolution. Cloaking hides it while it stays where it is, still
    // drawing and letting clicks through. Minimizing would stop the picture, and so does moving the window
    // off screen, which freezes the thumbnail on its last frame. Leaving it in place also means full screen
    // needn't be left first, so going in and out never makes Citrix renegotiate the display.

    private void SetPictureInPicture(bool on)
    {
        if (on == _pip is not null) return;
        if (on) EnterPictureInPicture();
        else ReturnFromPictureInPicture();
    }

    private bool CanEnterPictureInPicture => _host is not null && _sessionVisible && !_hidden && !_gaveUp && !_exitRequested && _pip is null;

    private bool ShrinksInsteadOfMinimizing => _viewerSettings.AutoPictureInPicture != "Off" && CanEnterPictureInPicture;

    /// <summary>
    /// A minimized window stops drawing, and picture in picture needs it drawing, so this has to happen instead of
    /// the minimize. That's the title bar's button and the taskbar's, which both arrive here.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE && ShrinksInsteadOfMinimizing)
        {
            DispatcherQueue.TryEnqueue(() => SetPictureInPicture(true));
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>The tray asked: a desktop that was sent to Citrium is already cloaked, so it only has to stop being hidden.</summary>
    private void PictureInPictureFromTray()
    {
        if (_hidden && _cloakedAway) _hidden = false;
        SetPictureInPicture(true);
    }

    private void EnterPictureInPicture()
    {
        if (!CanEnterPictureInPicture) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!Cloak(hwnd, true))
        {
            Log.Info($"Viewer[{_options.Title}] couldn't hide for picture in picture");
            return;
        }

        _edgeTimer.Stop();
        _toolbar?.AppWindow.Hide();
        _settingsPanel?.Hide();
        WindowHelper.SetShownInSwitchers(this, false);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        _pip = new PictureInPictureWindow(_options.Title, hwnd, _host!.Container, workArea, _viewerSettings.PictureInPictureOpacity);
        _pip.ReturnRequested += () => SetPictureInPicture(false);
        _pip.OpacityChanged += opacity =>
        {
            _viewerSettings.PictureInPictureOpacity = opacity;
            SettingsService.SaveViewer(_viewerSettings);
        };
        _pip.Activate(); // takes the keyboard, so nothing typed goes to a session that can't be seen
        Log.Info($"Viewer[{_options.Title}] in picture in picture");
    }

    private void ReturnFromPictureInPicture()
    {
        ClosePictureInPicture();
        _cloakedAway = false;
        ComeBackFromCloak();
    }

    private void ComeBackFromCloak()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Cloak(hwnd, false);
        WindowHelper.SetShownInSwitchers(this, true);
        Activate();
        SetForegroundWindow(hwnd);
        if (_fullScreen)
        {
            _edgeTimer.Start();
            ShowToolbar(_toolbar is { IsPinned: true } ? null : TimeSpan.FromSeconds(2.5));
        }
        PositionSession();
        _host?.Focus();
    }

    private static bool Cloak(IntPtr hwnd, bool on)
    {
        var value = on ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
    }

    private void ClosePictureInPicture()
    {
        try { _pip?.Leave(); } catch (Exception ex) { Log.Error("Couldn't close picture in picture", ex); }
        _pip = null;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettingsPanel((FrameworkElement)sender);

    private static string? CitrixPreferencesPath()
    {
        var engine = HdxEngine.Locate(AppState.Current.Settings.HdxEnginePath);
        if (engine is null) return null;
        var dir = Path.GetDirectoryName(engine)!;
        return new[]
        {
            Path.Combine(dir, "Citrix.PrefUI.App.exe"),
            Path.Combine(dir, "Receiver", "PrefPanel.exe"),
            Path.Combine(dir, "Legacy", "PrefUI.App.exe"),
        }.FirstOrDefault(File.Exists);
    }

    private void OpenCitrixPreferences()
    {
        if (_fullScreen) SetFullScreen(false); // Citrix's window would otherwise open behind the session
        if (CitrixPreferencesPath() is { } path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Couldn't open Citrix preferences", ex); }
        }
    }

    private void ShowSettingsPanel(FrameworkElement anchor) => ToggleSettingsPanel(() =>
    {
        var scale = anchor.XamlRoot?.RasterizationScale ?? 1.0;
        var corner = anchor.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(anchor.ActualWidth, anchor.ActualHeight));
        var pt = new POINT { X = (int)Math.Round(corner.X * scale), Y = (int)Math.Round(corner.Y * scale) };
        ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref pt);
        return new PanelAnchor(pt.X + (int)(4 * scale), pt.Y + (int)(6 * scale), true, false);
    });

    private void ToggleSettingsPanel(Func<PanelAnchor> anchor)
    {
        if (_settingsPanel is { IsShown: true }) { _settingsPanel.Hide(); return; }
        if (_settingsPanel is null)
        {
            _settingsPanel = new ViewerSettingsPanel(_viewerSettings, CitrixPreferencesPath() is not null);
            _settingsPanel.Changed += ApplyViewerSettings;
            _settingsPanel.OpenCitrixPreferences += OpenCitrixPreferences;
        }
        _settingsPanel.ShowAt(anchor());
    }

    private void ShowToolbar(TimeSpan? autoHide = null)
    {
        if (_toolbar is null) return;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _toolbar.ShowOn(new Windows.Graphics.RectInt32(pos.X, pos.Y, size.Width, size.Height));
        _toolbarHideAt = autoHide is { } d ? DateTime.UtcNow + d : DateTime.MaxValue;
    }

    private static readonly TimeSpan EdgeRevealDwell = TimeSpan.FromMilliseconds(400);
    private DateTime _atEdgeSince = DateTime.MaxValue;

    /// <summary>
    /// Resting the pointer at the screen edge the toolbar lives on reveals it; it hides again once the pointer leaves
    /// it. A floating toolbar comes back from the top edge. Polling the cursor (instead of relying on the engine to
    /// forward mouse input) is what makes it impossible for the toolbar to get "lost".
    /// Edges are busy places in a remote desktop: title bars, taskbars and scrollbars all live there. So it takes a
    /// pause, and never a held button: otherwise the toolbar lands under the pointer and takes the click or drag
    /// that was meant for the session.
    /// </summary>
    private void TrackToolbarEdge()
    {
        if (!_fullScreen || _toolbar is null || _toolbar.IsDragging) return;
        if (!GetCursorPos(out var pt)) return;

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        int right = pos.X + size.Width, bottom = pos.Y + size.Height;
        var onOurMonitor = pt.X >= pos.X && pt.X < right && pt.Y >= pos.Y && pt.Y < bottom;
        var dock = _toolbar.Dock;
        var atEdge = onOurMonitor && dock switch
        {
            ToolbarDock.Bottom => pt.Y >= bottom - 2,
            ToolbarDock.Left => pt.X <= pos.X + 1,
            ToolbarDock.Right => pt.X >= right - 2,
            _ => pt.Y <= pos.Y + 1,
        };

        if (atEdge)
        {
            if (!_toolbar.IsShown && IsMouseButtonDown())
            {
                _atEdgeSince = DateTime.MaxValue; // a pause only counts once the button is up again
                return;
            }
            if (_atEdgeSince == DateTime.MaxValue) _atEdgeSince = DateTime.UtcNow;
            var settled = DateTime.UtcNow - _atEdgeSince >= EdgeRevealDwell;
            if (!_toolbar.IsShown && !settled) return;
            if (!_toolbar.IsShown) Log.Info($"Pointer at {dock} edge ({pt.X},{pt.Y}); showing toolbar");
            if (!_toolbar.IsShown) ShowToolbar();
            _toolbarHideAt = DateTime.MaxValue;
            return;
        }
        _atEdgeSince = DateTime.MaxValue;

        if (!_toolbar.IsShown || _toolbar.IsPinned || _settingsPanel is { IsShown: true }) return;
        if (_toolbar.ContainsScreenPoint(pt.X, pt.Y, margin: 24))
        {
            _toolbarHideAt = DateTime.MaxValue;
        }
        else if (_toolbarHideAt == DateTime.MaxValue)
        {
            _toolbarHideAt = DateTime.UtcNow.AddMilliseconds(600);
        }
        else if (DateTime.UtcNow >= _toolbarHideAt)
        {
            _toolbar.AppWindow.Hide();
            _toolbarHideAt = DateTime.MaxValue;
            _host?.Focus();
        }
    }

    private static bool IsMouseButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

    private void CtrlAltDel_Click(object sender, RoutedEventArgs e) => _ = _host?.SendCtrlAltDelAsync();
    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(true);
    private void PictureInPicture_Click(object sender, RoutedEventArgs e) => SetPictureInPicture(true);
    private void SendToCitrium_Click(object sender, RoutedEventArgs e) => HideToSession();
    private void Disconnect_Click(object sender, RoutedEventArgs e) => _ = DisconnectAndExitAsync();
    private void StatusClose_Click(object sender, RoutedEventArgs e) => ExitViewer();

    /// <summary>
    /// The window's close button hides it, as Send to Citrium does: the desktop stays connected, and Show in
    /// Citrium opens this window again. Only Disconnect, Citrium exiting or the session ending close it for good.
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested) return;
        args.Cancel = true;
        if (_disconnecting) return;
        if (_host is { IsSessionAlive: true } && !_gaveUp)
        {
            HideToSession();
            return;
        }
        ExitViewer();
    }

    private void HideToSession()
    {
        if (_hidden) return;
        _hidden = true;
        Log.Info($"Viewer[{_options.Title}] hidden; session stays in Citrium");
        _edgeTimer.Stop();
        _toolbar?.AppWindow.Hide();
        _settingsPanel?.Hide();

        // Cloaked for the same reasons as picture in picture: the window keeps drawing, which is what Sessions
        // previews, and full screen needn't be left, so Citrix never renegotiates the display.
        if (Cloak(WinRT.Interop.WindowNative.GetWindowHandle(this), true))
        {
            _cloakedAway = true;
            WindowHelper.SetShownInSwitchers(this, false);
            return;
        }
        _restoreFullScreen = _fullScreen;
        if (_fullScreen) SetFullScreen(false);
        AppWindow.Hide();
    }

    private void PopOut()
    {
        if (_exitRequested) return;
        Log.Info($"Viewer[{_options.Title}] shown");
        SetPictureInPicture(false);
        if (_cloakedAway)
        {
            _cloakedAway = false;
            _hidden = false;
            ComeBackFromCloak();
            if (_restoreFullScreen)
            {
                _restoreFullScreen = false;
                SetFullScreen(true, onRememberedScreen: true);
            }
            if (_sessionVisible) _host?.Show(true);
            return;
        }
        AppWindow.Show();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        _hidden = false;
        Activate();
        SetForegroundWindow(hwnd);
        if (_restoreFullScreen)
        {
            _restoreFullScreen = false;
            SetFullScreen(true);
        }
        PositionSession();
        if (_sessionVisible) _host?.Show(true);
        _host?.Focus();
    }

    private async Task DisconnectAndExitAsync()
    {
        if (_disconnecting || _exitRequested) return;
        _disconnecting = true;
        Log.Info($"Viewer[{_options.Title}] disconnecting (adopted={_adopted})");
        _adoptTimer.Stop();
        _edgeTimer.Stop();
        _foregroundTimer.Stop();
        ClosePictureInPicture();
        _toolbar?.AppWindow.Hide();
        _settingsPanel?.Hide();
        AppWindow.Hide();
        // Disconnect only if the session is ours; after a fallback it lives on in Citrix Desktop Viewer.
        if (_host is not null && !_gaveUp && (_adopted || _host.IsSessionAlive))
        {
            try { await _host.DisconnectAsync(); } catch (Exception ex) { Log.Error("Disconnect failed", ex); }
        }
        ExitViewer();
    }

    private void ExitViewer()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _adoptTimer.Stop();
        _edgeTimer.Stop();
        _foregroundTimer.Stop();
        _altTab?.Dispose();
        _watchCts?.Cancel();
        ViewerRegistry.Unregister(Environment.ProcessId);
        ClosePictureInPicture();
        try { _toolbar?.Close(); } catch { }
        try { _settingsPanel?.Close(); } catch { }
        try { _host?.Dispose(); } catch { }
        _host = null;
        Application.Current.Exit();
    }

    private void StartSignals()
    {
        if (_disconnectMe is not null) return;
        var pid = Environment.ProcessId;
        (_disconnectMe, _disconnectAll, _showMe, _pictureInPictureMe) = ViewerRegistry.OpenSignals(pid);
        _disconnectMe.Reset();
        _showMe.Reset();
        _pictureInPictureMe.Reset();
        PublishRegistration();
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        Task.Run(() => WatchSignals(token));
    }

    private void PublishRegistration()
    {
        var engine = _host?.SurfaceProcessId ?? 0;
        if (engine == 0) engine = _host?.Engine?.Id ?? 0;
        ViewerRegistry.Register(new ViewerInfo(
            Environment.ProcessId, engine,
            WinRT.Interop.WindowNative.GetWindowHandle(this).ToInt64(),
            _options.Title, _options.Account, _host?.Container.ToInt64() ?? 0,
            _options.AccountId, _options.ResourceId, _ended, _endedFullScreen));
    }

    private void WatchSignals(CancellationToken token)
    {
        var handles = new WaitHandle[] { _disconnectMe!, _disconnectAll!, _showMe!, _pictureInPictureMe! };
        var misses = 0;
        while (!token.IsCancellationRequested)
        {
            var idx = WaitHandle.WaitAny(handles, 500);
            if (token.IsCancellationRequested) return;
            if (idx is 0 or 1)
            {
                DispatcherQueue.TryEnqueue(() => _ = DisconnectAndExitAsync());
                return;
            }
            if (idx == 2)
            {
                _showMe!.Reset();
                DispatcherQueue.TryEnqueue(PopOut);
                continue;
            }
            if (idx == 3)
            {
                _pictureInPictureMe!.Reset();
                DispatcherQueue.TryEnqueue(PictureInPictureFromTray);
                continue;
            }

            // Citrium gone (closed, or ended from the taskbar): this session exits with it.
            // Two misses, so one failed check doesn't drop a live desktop.
            if (ViewerRegistry.IsMainRunning()) misses = 0;
            else if (++misses >= 2)
            {
                DispatcherQueue.TryEnqueue(() => _ = DisconnectAndExitAsync());
                return;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private const uint WM_SYSCOMMAND = 0x112;
    private const long SC_MINIMIZE = 0xF020;
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private const int DWMWA_CLOAK = 13;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
}
