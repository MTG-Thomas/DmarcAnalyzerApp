using DmarcAnalyzer.Api.Application.Auth;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class PasskeyCeremonyStoreTests
{
    [Fact]
    public async Task CeremonyIsSecureStrictAndConsumedExactlyOnce()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(clock);
        var start = Context();
        var options = AssertionOptions(clock);

        store.StartAuthentication(start.Response, start.Request, options);
        var setCookie = start.Response.Headers.SetCookie.Single()!;
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=300", setCookie, StringComparison.OrdinalIgnoreCase);

        var cookie = setCookie.Split(';')[0];
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            var context = Context(cookie);
            return store.Consume(context.Request, context.Response, PasskeyCeremonyKind.Authentication);
        })).ToArray();
        await Task.WhenAll(attempts);

        Assert.Single(attempts.Select(x => x.Result), x => x is not null);
    }

    [Fact]
    public void CeremonyExpiresAfterFiveMinutes()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(clock);
        var start = Context();
        store.StartAuthentication(start.Response, start.Request, AssertionOptions(clock));
        var cookie = start.Response.Headers.SetCookie.Single()!.Split(';')[0];

        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(1)));
        var completion = Context(cookie);

        Assert.Null(store.Consume(completion.Request, completion.Response, PasskeyCeremonyKind.Authentication));
    }

    [Fact]
    public void DevelopmentHttpCeremonyCookieStillRequiresSecureTransport()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var store = new PasskeyCeremonyStore(
            new EphemeralDataProtectionProvider(),
            clock);
        var context = Context();
        context.Request.Scheme = "http";

        store.StartAuthentication(context.Response, context.Request, AssertionOptions(clock));

        Assert.Contains("secure", context.Response.Headers.SetCookie.Single()!, StringComparison.OrdinalIgnoreCase);
    }

    private static PasskeyCeremonyStore CreateStore(TimeProvider clock) => new(
        new EphemeralDataProtectionProvider(),
        clock);

    private static AssertionOptions AssertionOptions(TimeProvider _) => new Fido2(new Fido2Configuration
    {
        ServerDomain = "dmarc.midtowntg.com",
        ServerName = "DMARC Analyzer",
        Origins = new HashSet<string> { "https://dmarc.midtowntg.com" },
        ChallengeSize = 32,
    }).GetAssertionOptions(new GetAssertionOptionsParams
    {
        AllowedCredentials = [],
        UserVerification = UserVerificationRequirement.Required,
    });

    private static DefaultHttpContext Context(string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        return context;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

}
