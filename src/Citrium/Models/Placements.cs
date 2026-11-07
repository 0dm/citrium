namespace Citrium.Models;

/// <summary>Where a window was left, in physical pixels.</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>A point on the screen this desktop last went full screen on.</summary>
    public int? FullScreenX { get; set; }
    public int? FullScreenY { get; set; }
}

public sealed class Placements
{
    public WindowPlacement? PictureInPicture { get; set; }

    /// <summary>Keyed by <c>account|desktop</c>.</summary>
    public Dictionary<string, WindowPlacement> Desktops { get; set; } = [];
}
