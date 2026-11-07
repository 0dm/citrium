using System.ComponentModel;
using Citrium.Models;
using Citrium.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Citrium.Views;

public sealed partial class ResourcesPage : Page
{
    public enum Mode { Home, Favorites, Apps, Desktops, Search }

    private readonly AppState _state = AppState.Current;
    private Mode _mode;
    private string _query = "";
    private bool _suppressFilter;
    private TileMosaic? _mosaic;

    public ResourcesPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
        MosaicHost.LayoutUpdated += (_, _) => FitMosaic();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        switch (e.Parameter)
        {
            case Mode m: _mode = m; break;
            case string s when s.StartsWith("search:"): _mode = Mode.Search; _query = s["search:".Length..]; break;
            default: _mode = Mode.Home; break;
        }

        Header.Text = _mode switch
        {
            Mode.Home => "Home",
            Mode.Favorites => "Favorites",
            Mode.Apps => "Apps",
            Mode.Desktops => "Desktops",
            _ => $"Results for “{_query}”",
        };
        FolderFilter.Visibility = _mode is Mode.Apps or Mode.Desktops ? Visibility.Visible : Visibility.Collapsed;

        _state.ResourcesChanged += OnResourcesChanged;
        foreach (var a in _state.Accounts) a.PropertyChanged += OnAccountChanged;
        _state.Accounts.CollectionChanged += OnAccountsCollectionChanged;
        Rebuild();
    }

    private void Detach()
    {
        _state.ResourcesChanged -= OnResourcesChanged;
        foreach (var a in _state.Accounts) a.PropertyChanged -= OnAccountChanged;
        _state.Accounts.CollectionChanged -= OnAccountsCollectionChanged;
    }

    private void OnResourcesChanged(object? sender, EventArgs e) => Rebuild();
    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e) => UpdateSignInBar();
    private void OnAccountsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var a in _state.Accounts) { a.PropertyChanged -= OnAccountChanged; a.PropertyChanged += OnAccountChanged; }
        Rebuild();
    }

    private void Rebuild()
    {
        var all = _state.Resources.ToList();
        UpdateFolders(all);
        var folder = FolderFilter.SelectedItem as string;
        IEnumerable<StoreResource> Filter(IEnumerable<StoreResource> src) =>
            folder is null or "All categories" ? src : src.Where(r => r.Folder == folder);

        List<StoreResource> s1 = [], s2 = [], s3 = [];
        string? h1 = null, h2 = null, h3 = null;

        switch (_mode)
        {
            case Mode.Home:
                ShowMosaic(all);
                break;
            case Mode.Favorites:
                s1 = all.Where(r => r.IsFavorite).ToList();
                break;
            case Mode.Apps:
                s1 = Filter(all.Where(r => !r.IsDesktop)).ToList();
                break;
            case Mode.Desktops:
                s1 = Filter(all.Where(r => r.IsDesktop)).ToList();
                break;
            case Mode.Search:
                s1 = all.Where(r => r.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
                                 || r.Description.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
                                 || r.Folder.Contains(_query, StringComparison.CurrentCultureIgnoreCase)).ToList();
                break;
        }

        Bind(Section1Header, Section1, s1, h1);
        Bind(Section2Header, Section2, s2, h2);
        Bind(Section3Header, Section3, s3, h3);

        var total = s1.Count + s2.Count + s3.Count;
        ShowEmptyState(all.Count, total);
        UpdateSignInBar();
    }

    /// <summary>Everything once: favorites first and largest, then desktops, then apps.</summary>
    private void ShowMosaic(List<StoreResource> all)
    {
        if (_mosaic is null)
        {
            _mosaic = new TileMosaic();
            MosaicHost.Child = _mosaic;
            FitMosaic();
        }
        MosaicHost.Visibility = Visibility.Visible;

        var template = (DataTemplate)Resources["MosaicTileTemplate"];
        _mosaic.Show(
            all.OrderByDescending(r => r.IsFavorite).ThenByDescending(r => r.IsDesktop)
               .Select(r => new MosaicTile($"{r.AccountId}/{r.Id}", r, r.IsFavorite)).ToList(),
            () => (FrameworkElement)template.LoadContent());
    }

    /// <summary>
    /// The room below whatever is above the tiles, like the signed-out bar, which comes and goes. Measured after
    /// every layout, which settles straight away: the mosaic ignores a height that hasn't changed.
    /// </summary>
    private void FitMosaic()
    {
        if (_mosaic is null || _mosaic.Visibility != Visibility.Visible || Scroller.ActualHeight <= 0) return;
        var top = MosaicHost.TransformToVisual(Scroller).TransformPoint(new Windows.Foundation.Point(0, 0)).Y + Scroller.VerticalOffset;
        _mosaic.ViewportHeight = Math.Max(0, Scroller.ActualHeight - top - MosaicBottomMargin);
    }

    private const double MosaicBottomMargin = 12;

    private static void Bind(TextBlock header, GridView grid, List<StoreResource> items, string? title)
    {
        grid.ItemsSource = items;
        grid.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        header.Visibility = title is not null && items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (title is not null) header.Text = title;
    }

    private void UpdateFolders(List<StoreResource> all)
    {
        if (_mode is not (Mode.Apps or Mode.Desktops)) return;
        var folders = all.Where(r => r.IsDesktop == (_mode == Mode.Desktops))
                         .Select(r => r.Folder).Where(f => f.Length > 0).Distinct().OrderBy(f => f).ToList();
        var current = FolderFilter.SelectedItem as string;
        _suppressFilter = true;
        FolderFilter.ItemsSource = new[] { "All categories" }.Concat(folders).ToList();
        FolderFilter.SelectedItem = current is not null && folders.Contains(current) ? current : "All categories";
        FolderFilter.Visibility = folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _suppressFilter = false;
    }

    private void ShowEmptyState(int allCount, int shown)
    {
        EmptyAction.Visibility = Visibility.Collapsed;
        if (shown > 0 || (_mode == Mode.Home && allCount > 0))
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyState.Visibility = Visibility.Visible;

        if (_state.Accounts.Count == 0)
        {
            EmptyGlyph.Glyph = "";
            EmptyTitle.Text = "Add your Citrix account";
            EmptyText.Text = "Enter the address of your StoreFront store or Citrix Gateway that your organization gave you.";
            EmptyAction.Content = "Add account";
            EmptyAction.Tag = null;
            EmptyAction.Visibility = Visibility.Visible;
            return;
        }

        var needs = _state.Accounts.FirstOrDefault(a => a.Status is AccountStatus.NotSignedIn or AccountStatus.Error);
        if (allCount == 0 && needs is not null && !_state.Accounts.Any(a => a.IsBusy))
        {
            var failed = needs.Status == AccountStatus.Error;
            EmptyGlyph.Glyph = failed ? "" : "";
            EmptyTitle.Text = failed ? $"Can't connect to {needs.Name}" : $"Sign in to {needs.Name}";
            EmptyText.Text = failed ? needs.Error ?? "" : "Your apps and desktops appear here after you sign in.";
            EmptyAction.Content = failed ? "Try again" : "Sign in";
            EmptyAction.Tag = needs;
            EmptyAction.Visibility = Visibility.Visible;
            return;
        }

        (EmptyGlyph.Glyph, EmptyTitle.Text, EmptyText.Text) = _mode switch
        {
            _ when _state.Accounts.Any(a => a.IsBusy) => ("", "Loading…", "Fetching your apps and desktops."),
            _ when allCount == 0 => ("", "Nothing here yet", "Sign in to your account to see your apps and desktops."),
            Mode.Favorites => ("", "No favorites", "Right-click an app or desktop and choose “Add to favorites”."),
            Mode.Desktops => ("", "No desktops", "Your organization hasn't assigned you any desktops."),
            Mode.Apps => ("", "No apps", "Your organization hasn't assigned you any apps."),
            _ => ("", "No results", $"Nothing matches “{_query}”."),
        };
    }

    private void UpdateSignInBar()
    {
        var needs = _state.Accounts.FirstOrDefault(a => a.Status is AccountStatus.NotSignedIn or AccountStatus.Error);
        SignInBar.IsOpen = needs is not null && _state.Resources.Count > 0;
        if (needs is null) return;
        SignInBar.Tag = needs;
        SignInBar.Severity = needs.Status == AccountStatus.Error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        SignInBar.Title = needs.Status == AccountStatus.Error ? $"{needs.Name} is unavailable" : $"Signed out of {needs.Name}";
        SignInBar.Message = needs.Status == AccountStatus.Error ? needs.Error ?? "" : "Sign in to see your apps and desktops.";
    }

    private static StoreResource? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as StoreResource;

    private async void MosaicTile_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } r) await _state.LaunchAsync(r);
    }

    private async void Tile_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is StoreResource r) await _state.LaunchAsync(r);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } r) await _state.LaunchAsync(r);
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } r) _state.ToggleFavorite(r);
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } r) return;
        try
        {
            StartupService.CreateDesktopShortcut(r.Name, AppState.LinkTo(r));
            _state.Notify("Shortcut added", $"{r.Name} is on your desktop.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't create a desktop shortcut", ex);
            _state.Notify("Couldn't add the shortcut", ex.Message);
        }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } r) return;
        var panel = new StackPanel { Spacing = 8 };
        void Row(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.7 });
            panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        Row("Type", r.Kind);
        Row("Account", r.AccountName);
        Row("Category", r.Folder);
        Row("Description", r.Description);
        Row("Resource ID", r.Id);

        var dialog = new ContentDialog
        {
            Title = r.Name,
            Content = panel,
            PrimaryButtonText = "Open",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await _state.LaunchAsync(r);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _state.RefreshAllAsync(interactive: false);

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (SignInBar.Tag is AccountState a) await _state.RefreshAccountAsync(a, interactive: true);
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        if (EmptyAction.Tag is AccountState a) await _state.RefreshAccountAsync(a, interactive: true);
        else App.Current?.MainWindow?.ShowAccounts();
    }

    private void FolderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressFilter) Rebuild();
    }
}
