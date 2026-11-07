using Citrium.Services;
using Xunit;

namespace Citrium.Tests;

public class TileLayoutTests
{
    private const double Width = 1000, Height = 640;

    private static bool[] Plain(int count) => new bool[count];

    private static bool[] WithFavorites(int favorites, int others) =>
        [.. Enumerable.Repeat(true, favorites), .. Enumerable.Repeat(false, others)];

    private static void AssertNoOverlaps(TileArrangement a)
    {
        for (var i = 0; i < a.Tiles.Count; i++)
            for (var j = i + 1; j < a.Tiles.Count; j++)
            {
                var (p, q) = (a.Tiles[i], a.Tiles[j]);
                var apart = p.X + p.Width <= q.X || q.X + q.Width <= p.X || p.Y + p.Height <= q.Y || q.Y + q.Height <= p.Y;
                Assert.True(apart, $"tiles {i} and {j} overlap: {p} {q}");
            }
    }

    [Fact]
    public void NothingToShowTakesNoRoom()
    {
        Assert.Empty(TileLayout.Arrange([], Width, Height).Tiles);
        Assert.Empty(TileLayout.Arrange(Plain(3), 0, Height).Tiles);
    }

    [Fact]
    public void AFavoriteIsBiggerThanAnOrdinaryTile()
    {
        var a = TileLayout.Arrange(WithFavorites(1, 2), Width, Height);

        Assert.Equal(a.Tiles[1].Width * 2 + TileLayout.Gap, a.Tiles[0].Width);
        Assert.Equal(a.Tiles[0].Width, a.Tiles[0].Height);
    }

    [Fact]
    public void TilesShrinkAsMoreAreAdded()
    {
        var previous = double.MaxValue;
        foreach (var count in new[] { 1, 10, 30, 60 })
        {
            var cell = TileLayout.Arrange(Plain(count), Width, Height).Cell;
            Assert.True(cell <= previous, $"{count} tiles got bigger: {cell} after {previous}");
            previous = cell;
        }
        Assert.True(TileLayout.Arrange(Plain(60), Width, Height).Cell < TileLayout.Arrange(Plain(1), Width, Height).Cell);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(20)]
    public void WhatFitsDoesNotScroll(int count)
    {
        Assert.True(TileLayout.Arrange(Plain(count), Width, Height).Height <= Height);
    }

    [Fact]
    public void FavoritesGiveUpRoomBeforeThePageScrolls()
    {
        var a = TileLayout.Arrange(WithFavorites(6, 30), Width, Height);

        Assert.True(a.Height <= Height);
        Assert.True(a.Tiles[0].Height < a.Tiles[0].Width * 1.01 && a.Tiles[0].Width <= a.Cell * 2 + TileLayout.Gap);
    }

    [Fact]
    public void ScrollsOnlyWhenThereAreTooManyToFit()
    {
        var a = TileLayout.Arrange(Plain(500), Width, Height);

        Assert.True(a.Height > Height);
        Assert.True(a.Cell >= 96);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 9)]
    [InlineData(2, 1)]
    [InlineData(3, 12)]
    [InlineData(5, 40)]
    [InlineData(0, 200)]
    public void TilesNeverOverlapOrLeaveTheWidth(int favorites, int others)
    {
        var a = TileLayout.Arrange(WithFavorites(favorites, others), Width, Height);

        Assert.Equal(favorites + others, a.Tiles.Count);
        AssertNoOverlaps(a);
        Assert.All(a.Tiles, t => Assert.True(t.X >= 0 && t.Y >= 0 && t.X + t.Width <= Width + 0.001));
    }

    [Fact]
    public void AFavoriteStillFitsAPaneTooNarrowForTwoColumns()
    {
        var a = TileLayout.Arrange(WithFavorites(1, 3), 150, Height);

        AssertNoOverlaps(a);
        Assert.All(a.Tiles, t => Assert.True(t.X + t.Width <= 150.001));
    }

    [Fact]
    public void AnUnknownViewportHeightStillArrangesEverything()
    {
        Assert.Equal(8, TileLayout.Arrange(Plain(8), Width, 0).Tiles.Count);
    }
}
