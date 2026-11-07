using System.Numerics;
using Citrium.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Citrium.Views;

internal readonly record struct MosaicTile(string Key, object Item, bool Featured);

/// <summary>
/// Lays out Home's tiles with <see cref="TileLayout"/> and glides them to each new arrangement. The gliding is done
/// here, frame by frame, rather than with animations on the tiles: a tile that changes size has to lay its content
/// out again at every step, or its icon and label would jump at the end.
/// </summary>
internal sealed class TileMosaic : Panel
{
    private const double Speed = 13, // higher settles sooner
        SettledWithin = 0.4, FirstScale = 0.82;
    private const float HoverScale = 1.025f, PressScale = 0.97f;

    private sealed class Tile(string key, FrameworkElement element)
    {
        public string Key { get; } = key;
        public FrameworkElement Element { get; } = element;
        public bool Featured;
        public TileRect Current, Target;
        public bool Placed;
        public double Opacity;
    }

    private readonly List<Tile> _tiles = [];
    private readonly bool _animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private double _viewportHeight;
    private bool _gliding;
    private TimeSpan _lastFrame;

    public TileMosaic() => Unloaded += (_, _) => StopGliding();

    public double ViewportHeight
    {
        get => _viewportHeight;
        set
        {
            if (Math.Abs(_viewportHeight - value) < 0.5) return;
            _viewportHeight = value;
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// Makes the mosaic show these tiles, in this order. A tile that's still here keeps its element, even if a
    /// refresh replaced what it shows, so it moves instead of starting over.
    /// </summary>
    public void Show(IReadOnlyList<MosaicTile> tiles, Func<FrameworkElement> create)
    {
        var existing = _tiles.ToDictionary(t => t.Key);
        _tiles.Clear();
        Children.Clear();
        foreach (var next in tiles)
        {
            if (!existing.Remove(next.Key, out var tile))
            {
                tile = new Tile(next.Key, create());
                AddPressSpring(tile.Element);
            }
            tile.Featured = next.Featured;
            tile.Element.DataContext = next.Item;
            _tiles.Add(tile);
            Children.Add(tile.Element);
        }
        InvalidateMeasure();
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var arrangement = TileLayout.Arrange(_tiles.Select(t => t.Featured).ToList(), width, _viewportHeight);

        for (var i = 0; i < arrangement.Tiles.Count; i++)
        {
            var tile = _tiles[i];
            tile.Target = arrangement.Tiles[i];
            tile.Element.Measure(new Windows.Foundation.Size(tile.Target.Width, tile.Target.Height));
            if (tile.Placed) continue;

            // A new tile grows out of its own middle.
            tile.Placed = true;
            tile.Opacity = _animate ? 0 : 1;
            var (w, h) = (tile.Target.Width * FirstScale, tile.Target.Height * FirstScale);
            tile.Current = _animate
                ? new TileRect(tile.Target.X + (tile.Target.Width - w) / 2, tile.Target.Y + (tile.Target.Height - h) / 2, w, h)
                : tile.Target;
        }

        if (_animate && _tiles.Any(NeedsToMove)) StartGliding();
        else if (!_animate) foreach (var tile in _tiles) tile.Current = tile.Target;
        return new Windows.Foundation.Size(width, arrangement.Height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        foreach (var tile in _tiles)
        {
            tile.Element.Opacity = tile.Opacity;
            tile.Element.Arrange(new Windows.Foundation.Rect(tile.Current.X, tile.Current.Y, Math.Max(0, tile.Current.Width), Math.Max(0, tile.Current.Height)));
        }
        return finalSize;
    }

    private static bool NeedsToMove(Tile t) =>
        t.Opacity < 1
        || Math.Abs(t.Current.X - t.Target.X) > SettledWithin || Math.Abs(t.Current.Y - t.Target.Y) > SettledWithin
        || Math.Abs(t.Current.Width - t.Target.Width) > SettledWithin || Math.Abs(t.Current.Height - t.Target.Height) > SettledWithin;

    private void StartGliding()
    {
        if (_gliding) return;
        _gliding = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopGliding()
    {
        if (!_gliding) return;
        _gliding = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        if (e is not RenderingEventArgs { RenderingTime: var now } || now == _lastFrame) return;
        var seconds = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        // The same share of what's left every frame, whatever the frame rate: quick to start, gentle to land.
        var share = 1 - Math.Exp(-Speed * Math.Min(seconds, 0.05));
        double Toward(double from, double to) => Math.Abs(to - from) <= SettledWithin ? to : from + (to - from) * share;

        foreach (var tile in _tiles)
        {
            var (c, t) = (tile.Current, tile.Target);
            tile.Current = new TileRect(Toward(c.X, t.X), Toward(c.Y, t.Y), Toward(c.Width, t.Width), Toward(c.Height, t.Height));
            tile.Opacity = Math.Min(1, tile.Opacity + seconds * 4);
        }

        InvalidateArrange();
        if (!_tiles.Any(NeedsToMove)) StopGliding();
    }

    /// <summary>A tile leans toward the pointer and gives under a press, on a spring.</summary>
    private void AddPressSpring(FrameworkElement element)
    {
        if (!_animate) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);

        void Spring(float scale)
        {
            var compositor = visual.Compositor;
            visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
            var spring = compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(scale, scale, 1);
            spring.DampingRatio = 0.6f;
            spring.Period = TimeSpan.FromMilliseconds(40);
            visual.StartAnimation("Scale", spring);
        }

        element.PointerEntered += (_, _) => Spring(HoverScale);
        element.PointerExited += (_, _) => Spring(1);
        element.PointerCanceled += (_, _) => Spring(1);

        // Buttons mark these as handled, so a normal subscription would never hear them.
        element.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Spring(PressScale)), handledEventsToo: true);
        element.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => Spring(HoverScale)), handledEventsToo: true);
    }
}
