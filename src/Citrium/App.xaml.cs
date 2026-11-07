using Citrium.Services;
using Citrium.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Citrium;

public partial class App : Application
{
    private readonly string? _icaToOpen;
    private readonly ViewerOptions? _viewer;
    private MainWindow? _window;
    private ViewerWindow? _viewerWindow;

    public static new App? Current => Application.Current as App;
    public MainWindow? MainWindow => _window;
    public bool OpenSettingsOnLaunch { get; init; }
    internal CitriumLink? LinkToOpen { get; init; }
    public bool StartInBackground { get; init; }

    public App(string? icaToOpen, ViewerOptions? viewer = null)
    {
        _icaToOpen = icaToOpen;
        _viewer = viewer;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
            AppState.Current.Notify("Something went wrong", e.Exception.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_viewer is not null)
        {
            AppState.Current.LoadSettings();
            _viewerWindow = new ViewerWindow(_viewer);
            _viewerWindow.Activate();
            return;
        }

        Viewer.ViewerRegistry.MarkMainRunning();
        AppState.Current.Initialize(DispatcherQueue.GetForCurrentThread());
        Task.Run(StartupService.Refresh);
        _window = new MainWindow();
        _window.Activate();

        // Activated first, because the window's content doesn't load until it has been. Only with a tray
        // icon to come back from: otherwise this would be a running program with no way to reach it.
        if (StartInBackground && AppState.Current.Settings.MinimizeToTray) _window.AppWindow.Hide();

        if (_icaToOpen is not null) AppState.Current.LaunchIcaFile(_icaToOpen);
        if (OpenSettingsOnLaunch) _window.ShowSettings();
        if (LinkToOpen is { } link) _window.OpenLink(link);
    }

    /// <summary>Called (on a background thread) when a second instance forwards its activation.</summary>
    internal void OnRedirected(string? icaPath, bool showSettings = false, CitriumLink? link = null)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            // A link is about the thing it opens. Citrium only comes forward if that goes wrong.
            if (link is { } target) _window.OpenLink(target);
            else _window.BringToFront();
            if (showSettings) _window.ShowSettings();
            if (icaPath is not null) AppState.Current.LaunchIcaFile(icaPath);
        });
    }
}
