using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class PasskeyCeremonyStoreTests
{
    [Fact]
    public async Task CeremonyIsSecureStrictAndConsumedExactlyOnce()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, _) = CreateStore(clock);
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
        var (store, _) = CreateStore(clock);
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
        var (store, _) = CreateStore(clock);
        var context = Context();
        context.Request.Scheme = "http";

        store.StartAuthentication(context.Response, context.Request, AssertionOptions(clock));

        Assert.Contains("secure", context.Response.Headers.SetCookie.Single()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegistrationCeremonyRoundTripsChallengeAndUserId()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, _) = CreateStore(clock);
        var userId = Guid.NewGuid();
        var challenge = Enumerable.Repeat((byte)7, 32).ToArray();
        var start = Context();
        store.StartRegistration(start.Response, start.Request, userId, RegistrationOptions(userId, challenge));

        var completion = Context(CookieOf(start));
        var ceremony = store.Consume(completion.Request, completion.Response, PasskeyCeremonyKind.Registration);

        Assert.NotNull(ceremony);
        Assert.Equal(PasskeyCeremonyKind.Registration, ceremony.Kind);
        Assert.Equal(userId, ceremony.UserId);
        Assert.Equal(challenge, ceremony.RegistrationOptions!.Challenge);
        Assert.Equal("dmarc.midtowntg.com", ceremony.RegistrationOptions.Rp.Id);
        Assert.Equal(userId.ToByteArray(), ceremony.RegistrationOptions.User.Id);
        Assert.Null(ceremony.AuthenticationOptions);

        var replay = Context(CookieOf(start));
        Assert.Null(store.Consume(replay.Request, replay.Response, PasskeyCeremonyKind.Registration));
    }

    [Fact]
    public void AuthenticationCeremonyRebuildsVerificationPosture()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, _) = CreateStore(clock);
        var challenge = Enumerable.Repeat((byte)9, 32).ToArray();
        var start = Context();
        store.StartAuthentication(start.Response, start.Request, DirectAssertionOptions(challenge));

        var completion = Context(CookieOf(start));
        var ceremony = store.Consume(completion.Request, completion.Response, PasskeyCeremonyKind.Authentication);

        Assert.NotNull(ceremony);
        Assert.Equal(PasskeyCeremonyKind.Authentication, ceremony.Kind);
        Assert.Null(ceremony.UserId);
        Assert.Equal(challenge, ceremony.AuthenticationOptions!.Challenge);
        Assert.Equal("dmarc.midtowntg.com", ceremony.AuthenticationOptions.RpId);
        Assert.Equal(UserVerificationRequirement.Required, ceremony.AuthenticationOptions.UserVerification);
        Assert.Empty(ceremony.AuthenticationOptions.AllowCredentials);
        Assert.Null(ceremony.RegistrationOptions);
    }

    [Fact]
    public void WrongKindConsumeBurnsTheCeremony()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, _) = CreateStore(clock);
        var start = Context();
        store.StartAuthentication(start.Response, start.Request, DirectAssertionOptions(new byte[32]));
        var cookie = CookieOf(start);

        var wrongKind = Context(cookie);
        Assert.Null(store.Consume(wrongKind.Request, wrongKind.Response, PasskeyCeremonyKind.Registration));

        var rightKind = Context(cookie);
        Assert.Null(store.Consume(rightKind.Request, rightKind.Response, PasskeyCeremonyKind.Authentication));
    }

    [Fact]
    public void UnknownHandleConsumeIsNull()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var dataProtection = new EphemeralDataProtectionProvider();
        var (store, _) = CreateStore(clock, dataProtection);
        var forged = dataProtection.CreateProtector("dmarc-passkey-ceremony-v1").Protect("missing-handle");

        var completion = Context($"dmarc_passkey_ceremony={forged}");

        Assert.Null(store.Consume(completion.Request, completion.Response, PasskeyCeremonyKind.Authentication));
    }

    [Fact]
    public void StartRefusesWhenLiveCeremoniesAreAtCapacity()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, provider) = CreateStore(clock);
        SeedCeremonies(provider, live: 4096, expired: 10, clock.GetUtcNow().UtcDateTime);

        var start = Context();
        var exception = Assert.Throws<InvalidOperationException>(
            () => store.StartAuthentication(start.Response, start.Request, DirectAssertionOptions(new byte[32])));
        Assert.Equal("Too many passkey ceremonies are pending.", exception.Message);
    }

    [Fact]
    public void StartPurgesExpiredRowsFirst()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, provider) = CreateStore(clock);
        SeedCeremonies(provider, live: 0, expired: 1500, clock.GetUtcNow().UtcDateTime);

        var start = Context();
        store.StartAuthentication(start.Response, start.Request, DirectAssertionOptions(new byte[32]));

        // One purge batch (1000) ran ahead of the insert; the rest drain on
        // later starts rather than blocking this one.
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        Assert.Equal(501, db.PasskeyCeremonyStates.Count());
    }

    [Fact]
    public void PurgeExpiredRemovesOnlyExpiredRowsInBatches()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, provider) = CreateStore(clock);
        SeedCeremonies(provider, live: 2, expired: 3, clock.GetUtcNow().UtcDateTime);

        Assert.Equal(2, store.PurgeExpired(2));
        Assert.Equal(1, store.PurgeExpired());
        Assert.Equal(0, store.PurgeExpired());

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        Assert.Equal(2, db.PasskeyCeremonyStates.Count());
        Assert.All(db.PasskeyCeremonyStates, x => Assert.Null(x.ConsumedAtUtc));
    }

    [Fact]
    public void ConsumeRecordsAttemptsOnWinnerAndReplay()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, provider) = CreateStore(clock);
        var start = Context();
        store.StartAuthentication(start.Response, start.Request, DirectAssertionOptions(new byte[32]));
        var cookie = CookieOf(start);

        var first = Context(cookie);
        Assert.NotNull(store.Consume(first.Request, first.Response, PasskeyCeremonyKind.Authentication));
        var replay = Context(cookie);
        Assert.Null(store.Consume(replay.Request, replay.Response, PasskeyCeremonyKind.Authentication));

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        var row = db.PasskeyCeremonyStates.Single();
        Assert.Equal(2, row.Attempts);
        Assert.NotNull(row.ConsumedAtUtc);
    }

    private static (PasskeyCeremonyStore Store, ServiceProvider Provider) CreateStore(
        TimeProvider clock, IDataProtectionProvider? dataProtection = null)
    {
        // The name is hoisted: the options lambda runs per scope, so a Guid
        // inside it would give every scope its own database.
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        var provider = services.BuildServiceProvider();
        var store = new PasskeyCeremonyStore(
            dataProtection ?? new EphemeralDataProtectionProvider(),
            clock,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Fido2Configuration
            {
                ServerDomain = "dmarc.midtowntg.com",
                ServerName = "DMARC Analyzer",
                Origins = new HashSet<string> { "https://dmarc.midtowntg.com" },
            });
        return (store, provider);
    }

    private static void SeedCeremonies(ServiceProvider provider, int live, int expired, DateTime now)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        for (var i = 0; i < live; i++)
        {
            db.PasskeyCeremonyStates.Add(new PasskeyCeremonyState
            {
                Handle = $"live-{i}",
                Challenge = [(byte)i],
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(5),
            });
        }
        for (var i = 0; i < expired; i++)
        {
            db.PasskeyCeremonyStates.Add(new PasskeyCeremonyState
            {
                Handle = $"expired-{i}",
                Challenge = [(byte)i],
                CreatedAtUtc = now.AddMinutes(-10),
                ExpiresAtUtc = now.AddMinutes(-5),
            });
        }
        db.SaveChanges();
    }

    private static string CookieOf(HttpContext start) => start.Response.Headers.SetCookie.Single()!.Split(';')[0];

    private static AssertionOptions DirectAssertionOptions(byte[] challenge) => new()
    {
        Challenge = challenge,
        RpId = "dmarc.midtowntg.com",
        AllowCredentials = [],
        UserVerification = UserVerificationRequirement.Required,
    };

    private static CredentialCreateOptions RegistrationOptions(Guid userId, byte[] challenge) => new()
    {
        Rp = new PublicKeyCredentialRpEntity("dmarc.midtowntg.com", "DMARC Analyzer", null),
        User = new Fido2User { Id = userId.ToByteArray(), Name = "case@example.test", DisplayName = "Case" },
        Challenge = challenge,
        PubKeyCredParams = PubKeyCredParam.Defaults,
        AuthenticatorSelection = new AuthenticatorSelection
        {
            ResidentKey = ResidentKeyRequirement.Required,
            UserVerification = UserVerificationRequirement.Required,
        },
        Attestation = AttestationConveyancePreference.None,
        ExcludeCredentials = [],
    };

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

    [Fact]
    public void RegistrationCeremonyRoundTripsExactOptions()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var (store, _) = CreateStore(clock);
        var userId = Guid.NewGuid();
        var excluded = new byte[] { 9, 8, 7 };
        var start = Context();
        var options = RegistrationOptions(userId, Enumerable.Repeat((byte)3, 32).ToArray());
        options.ExcludeCredentials =
        [
            new PublicKeyCredentialDescriptor(
                PublicKeyCredentialType.PublicKey, excluded, [AuthenticatorTransport.Usb]),
        ];
        options.Timeout = 424242UL;
        store.StartRegistration(start.Response, start.Request, userId, options);

        var completion = Context(CookieOf(start));
        var ceremony = store.Consume(completion.Request, completion.Response, PasskeyCeremonyKind.Registration);

        // Bit-for-bit what creation produced: a future creation-param change
        // flows through the stored JSON instead of silently diverging from a
        // parallel rebuild.
        Assert.NotNull(ceremony);
        Assert.Equal(options.ToJson(), ceremony.RegistrationOptions!.ToJson());
        Assert.Equal(excluded, ceremony.RegistrationOptions.ExcludeCredentials.Single().Id);
        Assert.Equal((ulong)424242, ceremony.RegistrationOptions.Timeout);
        Assert.Equal("case@example.test", ceremony.RegistrationOptions.User.Name);
    }

    [Fact]
    public void LegacyRowWithoutOptionsJson_FallsBackToChallengeRebuild()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var dataProtection = new EphemeralDataProtectionProvider();
        var (store, provider) = CreateStore(clock, dataProtection);
        var challenge = Enumerable.Repeat((byte)5, 32).ToArray();
        var now = clock.GetUtcNow().UtcDateTime;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            db.PasskeyCeremonyStates.Add(new PasskeyCeremonyState
            {
                Handle = "legacy-handle",
                Challenge = challenge,
                OptionsJson = null,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(5),
            });
            db.SaveChanges();
        }

        var forged = dataProtection.CreateProtector("dmarc-passkey-ceremony-v1").Protect("legacy-handle");
        var completion = Context($"dmarc_passkey_ceremony={forged}");
        var ceremony = store.Consume(completion.Request, completion.Response, PasskeyCeremonyKind.Authentication);

        Assert.NotNull(ceremony);
        Assert.Equal(challenge, ceremony.AuthenticationOptions!.Challenge);
    }

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
