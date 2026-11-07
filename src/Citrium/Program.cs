using System.Text.RegularExpressions;
using Citrium.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Citrium;

/// <summary>
/// Custom entry point so Citrium runs as a single instance: launching it again (or opening an .ica file
/// with it) forwards the request to the window that is already open.
/// </summary>
public static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var viewer = Views.ViewerOptions.TryParse(args);
        if (viewer is null)
        {
            var key = AppInstance.FindOrRegisterForKey(AppPaths.InstanceName + ".Main");
            if (!key.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                Task.Run(() => key.RedirectActivationToAsync(activation).AsTask()).Wait();
                return 0;
            }
            key.Activated += (_, e) =>
            {
                var link = ExtractLink(e);
                App.Current?.OnRedirected(link is null ? ExtractIcaPath(e) : null, WantsSettings(e), link);
            };
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var link = viewer is null && CitriumLink.TryFind(string.Join(' ', args), out var found) ? found : (CitriumLink?)null;
            var ica = viewer is null && link is null ? args.FirstOrDefault(a => a.EndsWith(".ica", StringComparison.OrdinalIgnoreCase)) : null;
            _ = new App(ica, viewer)
            {
                OpenSettingsOnLaunch = viewer is null && args.Contains("--settings"),
                LinkToOpen = link,
                StartInBackground = viewer is null && args.Contains(StartupService.BackgroundArgument),
            };
        });
        return 0;
    }

    private static CitriumLink? ExtractLink(AppActivationArguments e)
    {
        var text = e.Data switch
        {
            ILaunchActivatedEventArgs launch when e.Kind == ExtendedActivationKind.Launch => launch.Arguments,
            IProtocolActivatedEventArgs protocol when e.Kind == ExtendedActivationKind.Protocol => protocol.Uri?.AbsoluteUri,
            _ => null,
        };
        return CitriumLink.TryFind(text, out var link) ? link : null;
    }

    private static string? ExtractIcaPath(AppActivationArguments e)
    {
        if (e.Kind == ExtendedActivationKind.Launch && e.Data is ILaunchActivatedEventArgs launch)
        {
            var m = IcaArg().Match(launch.Arguments ?? "");
            if (m.Success) return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
        if (e.Kind == ExtendedActivationKind.File && e.Data is IFileActivatedEventArgs file)
            return file.Files.FirstOrDefault()?.Path;
        return null;
    }

    private static bool WantsSettings(AppActivationArguments e) =>
        e.Kind == ExtendedActivationKind.Launch && e.Data is ILaunchActivatedEventArgs launch &&
        (launch.Arguments ?? "").Contains("--settings", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("\"([^\"]+\\.ica)\"|(\\S+\\.ica)", RegexOptions.IgnoreCase)]
    private static partial Regex IcaArg();
}
