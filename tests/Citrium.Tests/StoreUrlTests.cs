using Citrium.Services;
using Xunit;

namespace Citrium.Tests;

public class StoreUrlTests
{
    [Theory]
    [InlineData("citrix.contoso.com", "https://citrix.contoso.com/")]
    [InlineData("  citrix.contoso.com  ", "https://citrix.contoso.com/")]
    [InlineData("https://citrix.contoso.com/Citrix/StoreWeb/", "https://citrix.contoso.com/Citrix/StoreWeb/")]
    [InlineData("citrix.contoso.com.", "https://citrix.contoso.com/")]
    [InlineData("HTTPS://citrix.contoso.com", "https://citrix.contoso.com/")]
    public void TidiesAddresses(string typed, string expected)
    {
        Assert.Equal(expected, StoreUrl.Normalize(typed));
    }

    [Theory]
    [InlineData("http://citrix.contoso.com")]
    [InlineData("HTTP://citrix.contoso.com")]
    public void RefusesPlainHttp(string typed)
    {
        var error = Assert.Throws<FormatException>(() => StoreUrl.Normalize(typed));
        Assert.Contains("HTTPS", error.Message);
    }

    [Theory]
    [InlineData("ftp://citrix.contoso.com")]
    [InlineData("intranet")]
    [InlineData("https://")]
    [InlineData("")]
    public void RefusesWhatCannotBeAStore(string typed)
    {
        Assert.Throws<FormatException>(() => StoreUrl.Normalize(typed));
    }
}
