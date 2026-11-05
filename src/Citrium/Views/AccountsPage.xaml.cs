using Citrium.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;

namespace Citrium.Views;

public sealed partial class AccountsPage : Page
{
    private readonly AppState _state = AppState.Current;

    public AccountsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _state.Accounts.CollectionChanged -= OnChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _state.Accounts.CollectionChanged += OnChanged;
        OnChanged(null, null);
        if (PromptAddOnLoad)
        {
            PromptAddOnLoad = false;
            Loaded += OnLoadedPrompt;
        }
    }

    /// <summary>Set before navigating here to open the "Add account" dialog right away (first run).</summary>
    public static bool PromptAddOnLoad { get; set; }

    private async void OnLoadedPrompt(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedPrompt;
        await ShowAddDialogAsync();
    }

    private void OnChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        List.Children.Clear();
        foreach (var a in _state.Accounts) List.Children.Add(BuildCard(a));
        ListHeader.Visibility = _state.Accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Built in code rather than in an ItemsRepeater DataTemplate: Toolkit SettingsCard/SettingsExpander
    // content (the buttons) never got laid out when the cards were created from a template.
    private SettingsExpander BuildCard(AccountState a)
    {
        static Binding Bind(AccountState src, string path, IValueConverter? conv = null) =>
            new() { Source = src, Path = new PropertyPath(path), Mode = BindingMode.OneWay, Converter = conv };
        Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Tag = a };
            b.Click += click;
            return b;
        }
        var show = (IValueConverter)Application.Current.Resources["BoolToVisibility"];
        var hide = (IValueConverter)Application.Current.Resources["InverseBoolToVisibility"];

        var ring = new ProgressRing { Width = 20, Height = 20 };
        ring.SetBinding(ProgressRing.IsActiveProperty, Bind(a, nameof(AccountState.IsBusy)));
        var signIn = Btn("Sign in", SignIn_Click);
        signIn.SetBinding(VisibilityProperty, Bind(a, nameof(AccountState.IsSignedIn), hide));
        var refresh = Btn("Refresh", Refresh_Click);
        refresh.SetBinding(VisibilityProperty, Bind(a, nameof(AccountState.IsSignedIn), show));

        var address = new SettingsCard { Header = "Store address" };
        address.SetBinding(SettingsCard.DescriptionProperty, Bind(a, nameof(AccountState.Url)));

        var expander = new SettingsExpander
        {
            HeaderIcon = new FontIcon { Glyph = "" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ring, signIn, refresh } },
            Items =
            {
                address,
                new SettingsCard { Header = "Display name", Content = Btn("Rename", Rename_Click) },
                new SettingsCard { Header = "Sign out", Description = "End the store session and forget saved sign-in cookies.", Content = Btn("Sign out", SignOut_Click) },
                new SettingsCard { Header = "Remove account", Content = Btn("Remove", Remove_Click) },
            },
        };
        expander.SetBinding(SettingsExpander.HeaderProperty, Bind(a, nameof(AccountState.Name)));
        expander.SetBinding(SettingsExpander.DescriptionProperty, Bind(a, nameof(AccountState.StatusText)));
        return expander;
    }

    private static AccountState? Of(object sender) => (sender as FrameworkElement)?.Tag as AccountState;

    private async void Add_Click(object sender, RoutedEventArgs e) => await ShowAddDialogAsync();

    public async Task ShowAddDialogAsync()
    {
        var url = new TextBox { Header = "Store or gateway address", PlaceholderText = "citrix.contoso.com" };
        var name = new TextBox { Header = "Display name (optional)", PlaceholderText = "Work" };
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var help = new TextBlock
        {
            Text = "You'll sign in on your organization's page next. Any sign-in method it supports works " +
                   "(password, Citrix Gateway, multi-factor, Microsoft Entra ID / SAML).",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var dialog = new ContentDialog
        {
            Title = "Add account",
            Content = new StackPanel { Spacing = 12, Width = 400, Children = { url, name, help, error } },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        url.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = url.Text.Trim().Length > 0;

        string? normalized = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { normalized = AppState.NormalizeUrl(url.Text); }
            catch (FormatException ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && normalized is not null)
            await _state.AddAccountAsync(name.Text, normalized);
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (Of(sender) is { } a) await _state.RefreshAccountAsync(a, interactive: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Of(sender) is { } a) await _state.RefreshAccountAsync(a, interactive: true);
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (Of(sender) is { } a) await _state.SignOutAsync(a);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Of(sender) is not { } a) return;
        var box = new TextBox { Text = a.Name, Width = 360 };
        var dialog = new ContentDialog
        {
            Title = "Rename account",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        a.Account.Name = box.Text.Trim();
        a.NotifyNameChanged();
        _state.SaveSettings();
        await _state.RefreshAccountAsync(a, interactive: false);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Of(sender) is not { } a) return;
        var dialog = new ContentDialog
        {
            Title = $"Remove {a.Name}?",
            Content = "Its apps, desktops and favorites will be removed from Citrium.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await _state.RemoveAccountAsync(a);
    }
}
