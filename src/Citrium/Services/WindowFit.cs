namespace Citrium.Services;

internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

internal static class WindowFit
{
    /// <summary>
    /// Puts a saved window back on the screen it mostly sat on, kept whole inside that screen. Null if it was on a
    /// screen that's gone, so the caller can fall back to its default position rather than open a window nobody can reach.
    /// </summary>
    public static ScreenRect? Fit(ScreenRect saved, IReadOnlyList<ScreenRect> workAreas)
    {
        if (saved.Width <= 0 || saved.Height <= 0) return null;

        ScreenRect? best = null;
        long bestOverlap = 0;
        foreach (var area in workAreas)
        {
            long width = Math.Min(saved.Right, area.Right) - Math.Max(saved.X, area.X);
            long height = Math.Min(saved.Bottom, area.Bottom) - Math.Max(saved.Y, area.Y);
            if (width <= 0 || height <= 0 || width * height <= bestOverlap) continue;
            best = area;
            bestOverlap = width * height;
        }
        if (best is not { } screen) return null;

        var w = Math.Min(saved.Width, screen.Width);
        var h = Math.Min(saved.Height, screen.Height);
        return new ScreenRect(
            Math.Clamp(saved.X, screen.X, screen.Right - w),
            Math.Clamp(saved.Y, screen.Y, screen.Bottom - h),
            w, h);
    }
}
