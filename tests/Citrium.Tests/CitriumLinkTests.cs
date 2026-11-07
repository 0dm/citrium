using Citrium.Services;
using Xunit;

namespace Citrium.Tests;

public class CitriumLinkTests
{
    [Fact]
    public void RoundTripsAResourceIdFullOfAwkwardCharacters()
    {
        var link = new CitriumLink("abc123", "Store.Controller $S1-2 / Finance?x=1&y=2", FullScreen: true);

        Assert.True(CitriumLink.TryParse(link.ToUri(), out var parsed));
        Assert.Equal(link, parsed);
    }

    [Fact]
    public void FullScreenIsOffUnlessTheLinkAsks()
    {
        Assert.True(CitriumLink.TryParse("citrium://open/a/b", out var link));
        Assert.False(link.FullScreen);
    }

    [Fact]
    public void ToleratesTheTrailingSlashSomeBrowsersAdd()
    {
        Assert.True(CitriumLink.TryParse("citrium://open/a/b/", out var link));
        Assert.Equal("b", link.ResourceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("citrium://")]
    [InlineData("citrium://open/")]
    [InlineData("citrium://open/only-one-part")]
    [InlineData("citrium://open//missing-account")]
    [InlineData("citrium://open/a/b/c")]
    [InlineData("citrium://delete/a/b")]
    [InlineData("https://open/a/b")]
    public void RejectsAnythingThatIsNotAnOpenLink(string? text)
    {
        Assert.False(CitriumLink.TryParse(text, out _));
    }

    [Fact]
    public void RejectsAbsurdlyLongLinks()
    {
        Assert.False(CitriumLink.TryParse("citrium://open/a/" + new string('b', 4096), out _));
    }

    [Fact]
    public void FindsALinkInACommandLineWithTheProgramsOwnPath()
    {
        var uri = new CitriumLink("a", "b").ToUri();

        Assert.True(CitriumLink.TryFind($"\"C:\\Program Files\\Citrium\\Citrium.exe\" \"{uri}\"", out var link));
        Assert.Equal("b", link.ResourceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Citrium.exe --background")]
    [InlineData("Citrium.exe \"C:\\files\\desktop.ica\"")]
    public void FindsNothingInAnOrdinaryCommandLine(string? commandLine)
    {
        Assert.False(CitriumLink.TryFind(commandLine, out _));
    }
}
