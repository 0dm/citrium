using Citrium.Services;
using Xunit;

namespace Citrium.Tests;

public class UpdateServiceTests
{
    private const string Page = "https://github.com/0dm/citrium/releases/tag/v1.2.3";

    private static string Release(string tag = "v1.2.3", string url = Page, bool draft = false, bool prerelease = false) =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "{{url}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}} }""";

    private const string Digest = "sha256:298ABFE753D36F1A4127FCB9CFF1D7C7DF723233A394FE470B33D68454A5B1D5";
    private const string Msi = "https://github.com/0dm/citrium/releases/download/v1.2.3/Citrium-1.2.3-x64.msi";

    private static string ReleaseWith(string name = "Citrium-1.2.3-x64.msi", string url = Msi, string digest = Digest) =>
        $$"""
        { "tag_name": "v1.2.3", "html_url": "{{Page}}", "assets": [
          { "name": "Citrium-1.2.3-ARM64.msi", "browser_download_url": "https://github.com/0dm/citrium/releases/download/v1.2.3/Citrium-1.2.3-ARM64.msi", "digest": "sha256:{{new string('a', 64)}}" },
          { "name": "Citrium-win-x64.zip", "browser_download_url": "https://github.com/0dm/citrium/releases/download/v1.2.3/Citrium-win-x64.zip", "digest": "sha256:{{new string('b', 64)}}" },
          { "name": "{{name}}", "browser_download_url": "{{url}}", "digest": "{{digest}}" } ] }
        """;

    [Fact]
    public void FindsTheInstallerForThisArchitecture()
    {
        var installer = UpdateService.Parse(ReleaseWith(), "x64")?.Installer;

        Assert.NotNull(installer);
        Assert.Equal(Msi, installer.Url);
        Assert.Equal(Digest["sha256:".Length..].ToLowerInvariant(), installer.Sha256);
    }

    [Fact]
    public void FindsTheArmInstallerOnArm()
    {
        Assert.Contains("ARM64.msi", UpdateService.Parse(ReleaseWith(), "ARM64")?.Installer?.Url);
    }

    [Theory]
    [InlineData("https://evil.example/releases/download/v1.2.3/Citrium-1.2.3-x64.msi", Digest)]
    [InlineData("https://github.com/someone-else/citrium/releases/download/v1.2.3/Citrium-1.2.3-x64.msi", Digest)]
    [InlineData("http://github.com/0dm/citrium/releases/download/v1.2.3/Citrium-1.2.3-x64.msi", Digest)]
    [InlineData(Msi, "")]
    [InlineData(Msi, "sha256:abc")]
    [InlineData(Msi, "md5:298abfe753d36f1a4127fcb9cff1d7c7df723233a394fe470b33d68454a5b1d5")]
    public void WontInstallFromAnywhereElseOrWithoutADigest(string url, string digest)
    {
        var release = UpdateService.Parse(ReleaseWith(url: url, digest: digest), "x64");

        Assert.NotNull(release);
        Assert.Null(release.Installer);
    }

    [Fact]
    public void AReleaseWithoutAssetsHasNoInstaller()
    {
        Assert.Null(UpdateService.Parse(Release(), "x64")?.Installer);
    }

    [Fact]
    public void ChecksTheHashOfADownload()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "abc");

            Assert.True(UpdateService.HasHash(path, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Assert.False(UpdateService.HasHash(path, new string('0', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InstallsSilentlyThenReopensWhateverHappened()
    {
        var command = UpdateService.InstallCommand(@"C:\Windows\System32", @"C:\My Data\Citrium.msi", @"C:\My Data\install.log", @"C:\My Apps\Citrium.exe", "--background");

        Assert.Equal(
            """/d /s /c ""C:\Windows\System32\ping.exe" -n 3 127.0.0.1 >nul """ +
            """& start "" /wait "C:\Windows\System32\msiexec.exe" /i "C:\My Data\Citrium.msi" /qn /norestart MSIRESTARTMANAGERCONTROL=Disable /l*v "C:\My Data\install.log" """ +
            """& start "" "C:\My Apps\Citrium.exe" --background" """.TrimEnd(),
            command);
    }

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

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(-1, "Yesterday")]
    public void NamesRecentDays(int days, string name)
    {
        var now = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.FromHours(-5));
        var when = now.AddDays(days).AddHours(-1);

        Assert.Equal($"{name}, {when:t}", UpdateService.DescribeLastCheck(when, now));
    }

    [Fact]
    public void GivesTheDateForAnythingOlder()
    {
        var now = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
        var when = now.AddDays(-2);

        Assert.Equal($"{when:d}, {when:t}", UpdateService.DescribeLastCheck(when, now));
    }

    [Fact]
    public void JudgesTheDayInTheTimeZoneOfNow()
    {
        var now = new DateTimeOffset(2026, 11, 6, 1, 0, 0, TimeSpan.FromHours(9));
        var elsewhere = now.ToOffset(TimeSpan.FromHours(-5)).AddMinutes(-30); // Nov 5 there, Nov 6 here

        Assert.StartsWith("Today, ", UpdateService.DescribeLastCheck(elsewhere, now));
    }
}
