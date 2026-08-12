using DmarcAnalyzer.Api.Application.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class PasskeyOptionsTests
{
    private static PasskeyOptions Valid() => new()
    {
        Enabled = true,
        RelyingPartyId = "dmarc.midtowntg.com",
        RelyingPartyName = "DMARC Analyzer",
        Origins = ["https://dmarc.midtowntg.com"],
    };

    [Fact]
    public void ProductionConfigurationRequiresExactHttpsOrigin()
    {
        Assert.True(Valid().IsValid(isDevelopment: false));

        var broad = Valid();
        broad.RelyingPartyId = "midtowntg.com";
        Assert.False(broad.IsValid(isDevelopment: false));

        var insecure = Valid();
        insecure.Origins = ["http://dmarc.midtowntg.com"];
        Assert.False(insecure.IsValid(isDevelopment: false));

        var userInfo = Valid();
        userInfo.Origins = ["https://attacker@dmarc.midtowntg.com"];
        Assert.False(userInfo.IsValid(isDevelopment: false));
    }

    [Fact]
    public void LocalhostHttpIsDevelopmentOnly()
    {
        var local = new PasskeyOptions
        {
            Enabled = true,
            RelyingPartyId = "localhost",
            RelyingPartyName = "DMARC Analyzer local",
            Origins = ["http://localhost:5173"],
        };

        Assert.True(local.IsValid(isDevelopment: true));
        Assert.False(local.IsValid(isDevelopment: false));
    }

    [Theory]
    [InlineData("https://dmarc.midtowntg.com", true)]
    [InlineData("https://DMARC.MIDTOWNTG.COM", true)]
    [InlineData("https://dmarc.midtowntg.com/path", false)]
    [InlineData("https://dmarc.midtowntg.com?query=1", false)]
    [InlineData("https://other.midtowntg.com", false)]
    [InlineData("null", false)]
    public void RequestOriginMustMatchExactly(string origin, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Origin = origin;

        Assert.Equal(allowed, PasskeyRequestOrigin.IsAllowed(context.Request, Valid()));
    }
}
