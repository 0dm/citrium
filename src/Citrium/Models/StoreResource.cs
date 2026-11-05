using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace Citrium.Models;

public sealed class StoreResource : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Folder path as published (e.g. "\Office\").</summary>
    public string Path { get; init; } = "";
    public string? IconUrl { get; init; }
    public string? LaunchUrl { get; init; }
    public string? LaunchStatusUrl { get; init; }
    public bool IsDesktop { get; init; }
    public required string AccountId { get; init; }
    public string AccountName { get; init; } = "";

    public string Kind => IsDesktop ? "Desktop" : "App";
    public string Folder => Path.Trim('\\', '/').Replace('\\', ' ').Replace('/', ' ');
    public string Subtitle => string.IsNullOrWhiteSpace(Folder) ? AccountName : $"{AccountName} · {Folder}";
    public string FallbackGlyph => IsDesktop ? "" : "";

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasIcon)); OnPropertyChanged(nameof(NoIcon)); }
    }
    public bool HasIcon => _icon is not null;
    public bool NoIcon => _icon is null;

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteGlyph)); OnPropertyChanged(nameof(FavoriteLabel)); }
    }
    public string FavoriteGlyph => IsFavorite ? "" : "";
    public string FavoriteLabel => IsFavorite ? "Remove from favorites" : "Add to favorites";

    private bool _isLaunching;
    public bool IsLaunching
    {
        get => _isLaunching;
        set { _isLaunching = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
