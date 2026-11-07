namespace Citrium.Services;

internal readonly record struct TileRect(double X, double Y, double Width, double Height);

/// <param name="Tiles">One rectangle per tile, in the order they were given.</param>
/// <param name="Height">How tall the mosaic is. Taller than the viewport only once there are too many tiles to fit.</param>
internal sealed record TileArrangement(IReadOnlyList<TileRect> Tiles, double Height, double Cell);

/// <summary>
/// Sizes Home's mosaic in the manner of Windows' Start tiles: a square grid where a featured tile covers several
/// cells. It takes the largest cells that still fit everything in the viewport, so tiles start big and shrink as
/// more arrive. When they run out of room, featured tiles give up space before anything has to scroll.
/// </summary>
internal static class TileLayout
{
    public const double Gap = 8;
    private const double MaxCell = 190, ComfortableCell = 112, MinCell = 96;

    /// <summary>Featured tiles' shapes in cells, from most to least generous.</summary>
    private static readonly (int Columns, int Rows)[] FeaturedShapes = [(2, 2), (2, 1), (1, 1)];

    public static TileArrangement Arrange(IReadOnlyList<bool> featured, double width, double viewportHeight)
    {
        if (featured.Count == 0 || width <= 0) return new TileArrangement([], 0, 0);

        foreach (var shape in FeaturedShapes)
        {
            if (shape != (1, 1) && !featured.Contains(true)) continue;
            if (Best(featured, width, viewportHeight, shape) is { } fits) return fits;
        }

        // Too many to fit: the smallest comfortable size, and the page scrolls.
        var columns = Math.Max(1, ColumnsAt(MinCell, width));
        var cell = Math.Min(MaxCell, Cell(width, columns));
        return Pack(featured, columns, cell, (1, 1));
    }

    private static TileArrangement? Best(IReadOnlyList<bool> featured, double width, double viewportHeight, (int Columns, int Rows) shape)
    {
        var floor = shape == (1, 1) ? MinCell : ComfortableCell;
        TileArrangement? best = null;
        for (var columns = Math.Max(1, shape.Columns); columns <= Math.Max(1, ColumnsAt(floor, width)); columns++)
        {
            var cell = Math.Min(MaxCell, Cell(width, columns));
            if (cell < floor) continue;
            var candidate = Pack(featured, columns, cell, shape);
            if (viewportHeight > 0 && candidate.Height > viewportHeight) continue;

            // Past the largest cell size, more columns only make the mosaic flatter, like a row of Start tiles.
            if (best is null || cell > best.Cell || (cell == best.Cell && candidate.Height < best.Height)) best = candidate;
        }
        return best;
    }

    private static int ColumnsAt(double cell, double width) => (int)Math.Floor((width + Gap) / (cell + Gap));

    private static double Cell(double width, int columns) => (width - Gap * (columns - 1)) / columns;

    /// <summary>First fit, so small tiles tuck in beside a big one rather than leaving a hole.</summary>
    private static TileArrangement Pack(IReadOnlyList<bool> featured, int columns, double cell, (int Columns, int Rows) shape)
    {
        var taken = new List<bool[]>();
        var tiles = new List<TileRect>(featured.Count);
        var rowsUsed = 0;

        foreach (var isFeatured in featured)
        {
            var (w, h) = isFeatured ? (Math.Min(shape.Columns, columns), shape.Rows) : (1, 1);
            for (var row = 0; ; row++)
            {
                var placed = false;
                for (var column = 0; column + w <= columns && !placed; column++)
                {
                    if (!IsFree(taken, row, column, w, h, columns)) continue;
                    Take(taken, row, column, w, h, columns);
                    tiles.Add(new TileRect(column * (cell + Gap), row * (cell + Gap), w * cell + (w - 1) * Gap, h * cell + (h - 1) * Gap));
                    rowsUsed = Math.Max(rowsUsed, row + h);
                    placed = true;
                }
                if (placed) break;
            }
        }
        return new TileArrangement(tiles, rowsUsed * cell + Math.Max(0, rowsUsed - 1) * Gap, cell);
    }

    private static bool IsFree(List<bool[]> taken, int row, int column, int w, int h, int columns)
    {
        for (var r = row; r < row + h; r++)
        {
            if (r >= taken.Count) return true;
            for (var c = column; c < column + w; c++)
                if (taken[r][c]) return false;
        }
        return true;
    }

    private static void Take(List<bool[]> taken, int row, int column, int w, int h, int columns)
    {
        while (taken.Count < row + h) taken.Add(new bool[columns]);
        for (var r = row; r < row + h; r++)
            for (var c = column; c < column + w; c++)
                taken[r][c] = true;
    }
}
