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
        _window = new MainWindow();
        _window.Activate();

        if (_icaToOpen is not null) AppState.Current.LaunchIcaFile(_icaToOpen);
        if (OpenSettingsOnLaunch) _window.ShowSettings();
    }

    /// <summary>Called (on a background thread) when a second instance forwards its activation.</summary>
    public void OnRedirected(string? icaPath, bool showSettings = false)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            _window.BringToFront();
            if (showSettings) _window.ShowSettings();
            if (icaPath is not null) AppState.Current.LaunchIcaFile(icaPath);
        });
    }
}
