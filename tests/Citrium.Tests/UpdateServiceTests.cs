using Citrium.Services;
using Xunit;

namespace Citrium.Tests;

public class UpdateServiceTests
{
    private const string Page = "https://github.com/0dm/citrium/releases/tag/v1.2.3";

    private static string Release(string tag = "v1.2.3", string url = Page, bool draft = false, bool prerelease = false) =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "{{url}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}} }""";

    [Fact]
    public void ReadsARelease()
    {
        var release = UpdateService.Parse(Release());

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 3), release.Version);
        Assert.Equal(Page, release.Url);
    }

    [Fact]
    public void AcceptsATagWithoutAV()
    {
        Assert.Equal(new Version(1, 2, 3), UpdateService.Parse(Release(tag: "1.2.3"))?.Version);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SkipsDraftsAndPrereleases(bool draft, bool prerelease)
    {
        Assert.Null(UpdateService.Parse(Release(draft: draft, prerelease: prerelease)));
    }

    [Theory]
    [InlineData("https://evil.example/releases/v9.9.9")]
    [InlineData("https://github.com/someone-else/citrium/releases/tag/v9.9.9")]
    [InlineData("http://github.com/0dm/citrium/releases/tag/v9.9.9")]
    public void NeverPointsAnywhereButThisProjectsReleases(string url)
    {
        Assert.Null(UpdateService.Parse(Release(url: url)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": 5, "html_url": 6 }""")]
    public void ShrugsOffAnythingElse(string json)
    {
        Assert.Null(UpdateService.Parse(json));
    }

    [Fact]
    public void ShrugsOffATagThatIsNotAVersion()
    {
        Assert.Null(UpdateService.Parse(Release(tag: "nightly")));
    }

    [Theory]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("1.3.0", "1.2.9", true)]
    [InlineData("2.0", "1.9.9", true)]
    [InlineData("1.10.0", "1.9.0", true)] // numbers, not text
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2", "1.2.0.0", false)]
    [InlineData("1.2.2", "1.2.3", false)]
    public void ComparesVersions(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, UpdateService.IsNewer(Version.Parse(candidate), Version.Parse(current)));
    }
}
