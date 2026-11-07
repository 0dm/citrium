using System.ComponentModel;
using System.Runtime.InteropServices;
using Citrium.Services;
using Citrium.Viewer;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

namespace Citrium.Views;

/// <summary>
/// Everything running. Desktops in a Citrium viewer get a live preview: a DWM thumbnail placed over a card, so it's
/// positioned by hand, cut off at the scroll area's edges, and hidden while a dialog needs the space above it.
/// </summary>
public sealed partial class SessionsPage : Page
{
    private const double PreviewWidth = 224, PreviewHeight = 126;
    private const string DesktopGlyph = "", DisconnectedGlyph = "";

    private sealed record Preview(Border Host, HdxSession Session)
    {
        public DwmThumbnail? Thumbnail { get; set; }
    }

    private readonly SessionMonitor _monitor = AppState.Current.Sessions;
    private readonly List<Preview> _previews = [];
    private readonly List<Storyboard> _pulses = [];
    private readonly List<HdxSession> _watched = [];
    private readonly DispatcherQueueTimer _previewTimer;
    private bool _previewsPaused;

    public SessionsPage()
    {
        InitializeComponent();

        // Often enough to follow layout changes nothing announces, like a notification sliding the page down.
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(250);
        _previewTimer.Tick += (_, _) => UpdatePreviews();
        Scroller.ViewChanged += (_, _) => UpdatePreviews();
        SizeChanged += (_, _) => UpdatePreviews();

        Unloaded += (_, _) =>
        {
            _monitor.Sessions.CollectionChanged -= OnChanged;
            _previewTimer.Stop();
            ClearCards();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _monitor.Sessions.CollectionChanged += OnChanged;
        _monitor.Poll();
        OnChanged(null, null);
        _previewTimer.Start();
    }

    private void OnChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        ClearCards();
        foreach (var session in _monitor.Sessions)
        {
            session.PropertyChanged += OnSessionChanged;
            _watched.Add(session);
            List.Children.Add(session.Viewer is null ? BuildCard(session) : BuildViewerCard(session));
        }
        EmptyState.Visibility = _monitor.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HdxSession.IsEnded)) OnChanged(null, null);
    }

    private void ClearCards()
    {
        foreach (var session in _watched) session.PropertyChanged -= OnSessionChanged;
        _watched.Clear();
        foreach (var pulse in _pulses) pulse.Stop();
        _pulses.Clear();
        foreach (var preview in _previews) preview.Thumbnail?.Dispose();
        _previews.Clear();
        List.Children.Clear();
    }

    private Button CommandButton(string text, HdxSession session, RoutedEventHandler click, bool accent = false)
    {
        var b = new Button { Content = text, Tag = session };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        b.Click += click;
        return b;
    }

    // Built in code: Toolkit SettingsCard content doesn't get laid out when created from a DataTemplate.
    private SettingsCard BuildCard(HdxSession session)
    {
        var card = new SettingsCard
        {
            Header = session.Name,
            HeaderIcon = new FontIcon { Glyph = session.Glyph },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { CommandButton("Show", session, Show_Click), CommandButton("Disconnect", session, Disconnect_Click) },
            },
        };
        card.SetBinding(SettingsCard.DescriptionProperty,
            new Binding { Source = session, Path = new PropertyPath(nameof(HdxSession.Detail)), Mode = BindingMode.OneWay });
        return card;
    }

    private Border BuildViewerCard(HdxSession session)
    {
        var host = new Border
        {
            Width = PreviewWidth, Height = PreviewHeight,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            Child = new FontIcon
            {
                Glyph = session.IsEnded ? DisconnectedGlyph : DesktopGlyph,
                FontSize = 28, Opacity = 0.5,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        if (!session.IsEnded)
        {
            _previews.Add(new Preview(host, session));
            ToolTipService.SetToolTip(host, "Show this desktop");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, $"Show {session.Name}");
            host.Tapped += (_, _) => SessionMonitor.BringToFront(session);
        }

        var name = new TextBlock
        {
            Text = session.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        var detail = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.75,
        };
        detail.SetBinding(TextBlock.TextProperty,
            new Binding { Source = session, Path = new PropertyPath(nameof(HdxSession.Detail)), Mode = BindingMode.OneWay });

        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        if (session.IsEnded)
        {
            commands.Children.Add(CommandButton("Reconnect", session, Reconnect_Click, accent: true));
            commands.Children.Add(CommandButton("Dismiss", session, Dismiss_Click));
        }
        else
        {
            commands.Children.Add(CommandButton("Show", session, Show_Click));
            commands.Children.Add(CommandButton("Picture in picture", session, PictureInPicture_Click));
            commands.Children.Add(CommandButton("Disconnect", session, Disconnect_Click));
        }

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
            Children = { name, StatusLine(session), detail, commands },
        };
        Grid.SetColumn(text, 1);

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = new Grid
            {
                ColumnSpacing = 16,
                ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } },
                Children = { host, text },
            },
        };
    }

    /// <summary>A small dot that breathes while the desktop is live, and holds still once it isn't.</summary>
    private StackPanel StatusLine(HdxSession session)
    {
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)Application.Current.Resources[session.IsEnded ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush"],
        };
        if (!session.IsEnded)
        {
            var breathe = new DoubleAnimation
            {
                From = 1, To = 0.35,
                Duration = new Duration(TimeSpan.FromSeconds(1.2)),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(breathe, dot);
            Storyboard.SetTargetProperty(breathe, "Opacity");
            var pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, Children = { breathe } };
            pulse.Begin();
            _pulses.Add(pulse);
        }
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                dot,
                new TextBlock
                {
                    Text = session.IsEnded ? "Slipped away" : "Live",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                },
            },
        };
    }

    private void UpdatePreviews()
    {
        if (_previewsPaused || XamlRoot is null || !IsLoaded) return;
        var scale = XamlRoot.RasterizationScale;
        var visibleArea = BoundsOf(Scroller, scale);
        var destination = WinRT.Interop.WindowNative.GetWindowHandle(App.Current!.MainWindow!);

        foreach (var preview in _previews)
        {
            if (preview.Session.Viewer is not { WindowHandle: not 0, SessionHandle: not 0 } viewer)
            {
                preview.Thumbnail?.Hide();
                continue;
            }

            var window = new IntPtr(viewer.WindowHandle);
            var source = DwmThumbnail.ChildRect(window, new IntPtr(viewer.SessionHandle));
            if (IsIconic(window) || source.Width <= 1 || source.Height <= 1) // a minimized window stops drawing
            {
                preview.Thumbnail?.Hide();
                continue;
            }

            preview.Thumbnail ??= DwmThumbnail.TryCreate(destination, window);
            preview.Thumbnail?.Show(DwmThumbnail.Letterbox(source, BoundsOf(preview.Host, scale)), source, visibleArea);
        }
    }

    private static NativeRect BoundsOf(FrameworkElement element, double scale)
    {
        var origin = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        int left = (int)Math.Round(origin.X * scale), top = (int)Math.Round(origin.Y * scale);
        return new NativeRect
        {
            Left = left, Top = top,
            Right = left + (int)Math.Round(element.ActualWidth * scale),
            Bottom = top + (int)Math.Round(element.ActualHeight * scale),
        };
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HdxSession s) SessionMonitor.BringToFront(s);
    }

    private void PictureInPicture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HdxSession { Viewer: { } viewer }) ViewerRegistry.RequestPictureInPicture(viewer.ViewerPid);
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HdxSession { Viewer: { AccountId: { } account, ResourceId: { } resource } viewer })
            App.Current?.MainWindow?.OpenLink(new CitriumLink(account, resource, viewer.WasFullScreen));
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HdxSession s) _monitor.Disconnect(s);
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HdxSession s) return;
        var dialog = new ContentDialog
        {
            Title = $"Disconnect {s.Name}?",
            Content = "Unsaved work stays open on the server if your organization keeps disconnected sessions. " +
                      "Launch it again to reconnect.",
            PrimaryButtonText = "Disconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };

        // The pictures are drawn above everything in this window, dialogs included.
        _previewsPaused = true;
        foreach (var preview in _previews) preview.Thumbnail?.Hide();
        var result = await dialog.ShowAsync();
        _previewsPaused = false;

        if (result == ContentDialogResult.Primary) _monitor.Disconnect(s);
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
