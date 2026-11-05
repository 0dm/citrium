using Citrium.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;

namespace Citrium.Views;

public sealed partial class SessionsPage : Page
{
    private readonly SessionMonitor _monitor = AppState.Current.Sessions;

    public SessionsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _monitor.Sessions.CollectionChanged -= OnChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _monitor.Sessions.CollectionChanged += OnChanged;
        _monitor.Poll();
        OnChanged(null, null);
    }

    private void OnChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        List.Children.Clear();
        foreach (var session in _monitor.Sessions) List.Children.Add(BuildCard(session));
        EmptyState.Visibility = _monitor.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Built in code: Toolkit SettingsCard content doesn't get laid out when created from a DataTemplate.
    private SettingsCard BuildCard(HdxSession session)
    {
        Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Tag = session };
            b.Click += click;
            return b;
        }
        var card = new SettingsCard
        {
            Header = session.Name,
            HeaderIcon = new FontIcon { Glyph = session.Glyph },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Btn("Show", Show_Click), Btn("Disconnect", Disconnect_Click) } },
        };
        card.SetBinding(SettingsCard.DescriptionProperty,
            new Binding { Source = session, Path = new PropertyPath(nameof(HdxSession.Detail)), Mode = BindingMode.OneWay });
        return card;
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HdxSession s) SessionMonitor.BringToFront(s);
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _monitor.Disconnect(s);
    }
}
