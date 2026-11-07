using Citrium.Services;
using Xunit;

namespace Citrium.Tests;

public class WindowFitTests
{
    private static readonly ScreenRect Left = new(0, 0, 1920, 1040);
    private static readonly ScreenRect Right = new(1920, 0, 2560, 1400);

    [Fact]
    public void LeavesAWindowThatStillFitsExactlyWhereItWas()
    {
        var saved = new ScreenRect(100, 100, 800, 600);
        Assert.Equal(saved, WindowFit.Fit(saved, [Left, Right]));
    }

    [Fact]
    public void ReturnsNullWhenItsScreenIsGone()
    {
        Assert.Null(WindowFit.Fit(new ScreenRect(2100, 100, 800, 600), [Left]));
    }

    [Fact]
    public void PullsAStraddlingWindowOntoTheScreenItWasMostlyOn()
    {
        var fitted = WindowFit.Fit(new ScreenRect(1800, 100, 800, 600), [Left, Right]);
        Assert.Equal(new ScreenRect(1920, 100, 800, 600), fitted);
    }

    [Fact]
    public void ShrinksAWindowFromABiggerScreenToFitASmallerOne()
    {
        var fitted = WindowFit.Fit(new ScreenRect(-100, -100, 3000, 2000), [Left]);
        Assert.Equal(Left, fitted);
    }

    [Fact]
    public void WorksOnAScreenToTheLeftOfThePrimaryOne()
    {
        var screen = new ScreenRect(-1920, 0, 1920, 1040);
        var saved = new ScreenRect(-1000, 200, 500, 400);
        Assert.Equal(saved, WindowFit.Fit(saved, [screen, Left]));
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(-5, 600)]
    public void IgnoresASizeThatWasNeverReal(int width, int height)
    {
        Assert.Null(WindowFit.Fit(new ScreenRect(100, 100, width, height), [Left]));
    }

    [Fact]
    public void FindsNothingWithNoScreens()
    {
        Assert.Null(WindowFit.Fit(new ScreenRect(0, 0, 100, 100), []));
    }
}
