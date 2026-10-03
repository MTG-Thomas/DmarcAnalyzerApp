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

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class AuthDurabilityTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task ReplicaAStart_ReplicaBComplete()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        // One shared ring for both hosts, standing in for the durable ring
        // production gets from AddDurableDataProtection (covered below).
        var ring = new EphemeralDataProtectionProvider();
        using var replicaA = BuildCeremonyHost(ring);
        using var replicaB = BuildCeremonyHost(ring);

        var challenge = Enumerable.Repeat((byte)11, 32).ToArray();
        var start = Context();
        replicaA.GetRequiredService<IPasskeyCeremonyStore>()
            .StartAuthentication(start.Response, start.Request, AssertionOptions(challenge));

        var completion = Context(CookieOf(start));
        var ceremony = replicaB.GetRequiredService<IPasskeyCeremonyStore>()
            .Consume(completion.Request, completion.Response, PasskeyCeremonyKind.Authentication);

        Assert.NotNull(ceremony);
        Assert.Equal(challenge, ceremony.AuthenticationOptions!.Challenge);
    }

    [Fact]
    public async Task DoubleConsumeRace_OneWins()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var ring = new EphemeralDataProtectionProvider();
        using var replicaA = BuildCeremonyHost(ring);
        using var replicaB = BuildCeremonyHost(ring);

        var start = Context();
        replicaA.GetRequiredService<IPasskeyCeremonyStore>()
            .StartAuthentication(start.Response, start.Request, AssertionOptions(new byte[32]));
        var cookie = CookieOf(start);

        var attempts = await Task.WhenAll(
            Task.Run(() =>
            {
                var context = Context(cookie);
                return replicaA.GetRequiredService<IPasskeyCeremonyStore>()
                    .Consume(context.Request, context.Response, PasskeyCeremonyKind.Authentication);
            }),
            Task.Run(() =>
            {
                var context = Context(cookie);
                return replicaB.GetRequiredService<IPasskeyCeremonyStore>()
                    .Consume(context.Request, context.Response, PasskeyCeremonyKind.Authentication);
            }));

        Assert.Single(attempts, x => x is not null);
    }

    [Fact]
    public async Task DataProtection_RoundTripAcrossProviderInstances()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        using var first = BuildDataProtectionHost();
        var payload = first.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("pg-roundtrip")
            .Protect("replica-secret");

        using var second = BuildDataProtectionHost();
        var reopened = second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("pg-roundtrip")
            .Unprotect(payload);

        Assert.Equal("replica-secret", reopened);

        using var verification = BuildDataProtectionHost();
        using var scope = verification.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        Assert.True(await db.Set<DpKey>().CountAsync() >= 1);
    }

    [Fact]
    public async Task ExpiredCeremonyConsumeFailsAndPurgeRemovesIt()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var ring = new EphemeralDataProtectionProvider();
        using var host = BuildCeremonyHost(ring);
        var store = (PasskeyCeremonyStore)host.GetRequiredService<IPasskeyCeremonyStore>();

        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            var now = DateTime.UtcNow;
            db.PasskeyCeremonyStates.Add(new PasskeyCeremonyState
            {
                Handle = "expired-1",
                Challenge = [1, 2, 3],
                CreatedAtUtc = now.AddMinutes(-10),
                ExpiresAtUtc = now.AddMinutes(-1),
            });
            db.PasskeyCeremonyStates.Add(new PasskeyCeremonyState
            {
                Handle = "live-1",
                Challenge = [4, 5, 6],
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(5),
            });
            await db.SaveChangesAsync();
        }

        var protectedHandle = ring.CreateProtector("dmarc-passkey-ceremony-v1").Protect("expired-1");
        var completion = Context($"dmarc_passkey_ceremony={protectedHandle}");
        Assert.Null(store.Consume(completion.Request, completion.Response, PasskeyCeremonyKind.Authentication));

        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            Assert.Equal(1, await db.PasskeyCeremonyStates
                .Where(x => x.Handle == "expired-1")
                .Select(x => x.Attempts)
                .SingleAsync());
        }

        Assert.Equal(1, store.PurgeExpired());

        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            Assert.Equal("live-1", await db.PasskeyCeremonyStates.Select(x => x.Handle).SingleAsync());
        }
    }

    private ServiceProvider BuildCeremonyHost(IDataProtectionProvider ring)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseNpgsql(database.ConnectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Fido2Configuration());
        services.AddSingleton(ring);
        services.AddSingleton<IPasskeyCeremonyStore, PasskeyCeremonyStore>();
        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildDataProtectionHost()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseNpgsql(database.ConnectionString));
        services.AddDurableDataProtection();
        return services.BuildServiceProvider();
    }

    private static Fido2Configuration Fido2Configuration() => new()
    {
        ServerDomain = "dmarc.midtowntg.com",
        ServerName = "DMARC Analyzer",
        Origins = new HashSet<string> { "https://dmarc.midtowntg.com" },
    };

    private static AssertionOptions AssertionOptions(byte[] challenge) => new()
    {
        Challenge = challenge,
        RpId = "dmarc.midtowntg.com",
        AllowCredentials = [],
        UserVerification = UserVerificationRequirement.Required,
    };

    private static string CookieOf(HttpContext start) => start.Response.Headers.SetCookie.Single()!.Split(';')[0];

    private static DefaultHttpContext Context(string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        return context;
    }
}
