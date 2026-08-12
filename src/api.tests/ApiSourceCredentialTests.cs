using System.Security.Cryptography;
using System.Text;
using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.ReportSources;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ApiSourceCredentialTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    [Fact]
    public async Task IssuesRevealOnceTokenAndStoresOnlyPrefixAndHash()
    {
        await using var db = NewDb();
        var (_, source) = await SeedApiSourceAsync(db);

        var result = await new ApiSourceCredentialService(db).IssueAsync(source.Id, default);

        Assert.True(result.IsSuccess);
        var issued = result.Value!;
        Assert.Matches("^dmarc_v1\\.[A-Za-z0-9_-]{22}\\.[A-Za-z0-9_-]{43}$", issued.Token);

        var stored = await db.ApiSourceCredentials.SingleAsync();
        Assert.Equal(issued.Prefix, stored.Prefix);
        Assert.Equal(32, stored.TokenHash.Length);
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(issued.Token)), stored.TokenHash);
        Assert.DoesNotContain(
            typeof(ApiSourceCredential).GetProperties(),
            property => property.Name.Contains("Token", StringComparison.Ordinal)
                        && property.Name != nameof(ApiSourceCredential.TokenHash));
    }

    [Fact]
    public async Task AuthenticatesOnlyCorrectActiveApiSourceAndReturnsCanonicalContext()
    {
        await using var db = NewDb();
        var (client, source) = await SeedApiSourceAsync(db);
        var otherClient = new Client
        {
            Name = "Other client",
            Slug = "other-client",
            Timezone = "UTC",
        };
        var other = new ReportSource
        {
            Name = "Other API",
            Protocol = "api",
            DefaultClientId = otherClient.Id,
        };
        db.AddRange(otherClient, other);
        await db.SaveChangesAsync();

        var issued = (await new ApiSourceCredentialService(db).IssueAsync(source.Id, default)).Value!;
        var authenticator = new ApiSourceAuthenticator(db);

        var authenticated = await authenticator.AuthenticateAsync(source.Id, issued.Token, default);
        Assert.NotNull(authenticated);
        Assert.Equal(source.Id, authenticated.SourceId);
        Assert.Equal(client.Id, authenticated.DefaultClientId);
        Assert.True(authenticated.RestrictToDefaultClient);

        Assert.Null(await authenticator.AuthenticateAsync(source.Id, null, default));
        Assert.Null(await authenticator.AuthenticateAsync(source.Id, "wrong", default));
        var wrongToken = issued.Token[..^1] + (issued.Token[^1] == 'A' ? 'B' : 'A');
        Assert.Null(await authenticator.AuthenticateAsync(source.Id, wrongToken, default));
        Assert.Null(await authenticator.AuthenticateAsync(other.Id, issued.Token, default));

        source.IsActive = false;
        await db.SaveChangesAsync();
        Assert.Null(await authenticator.AuthenticateAsync(source.Id, issued.Token, default));
    }

    [Fact]
    public async Task RotationOverlapsUntilOneCredentialIsRevoked()
    {
        await using var db = NewDb();
        var (_, source) = await SeedApiSourceAsync(db);
        var service = new ApiSourceCredentialService(db);
        var first = (await service.IssueAsync(source.Id, default)).Value!;
        var second = (await service.IssueAsync(source.Id, default)).Value!;
        var authenticator = new ApiSourceAuthenticator(db);

        Assert.NotNull(await authenticator.AuthenticateAsync(source.Id, first.Token, default));
        Assert.NotNull(await authenticator.AuthenticateAsync(source.Id, second.Token, default));

        var revoked = await service.RevokeAsync(source.Id, first.Id, default);
        Assert.True(revoked.IsSuccess);
        Assert.NotNull(revoked.Value!.RevokedAtUtc);
        Assert.Null(await authenticator.AuthenticateAsync(source.Id, first.Token, default));
        Assert.NotNull(await authenticator.AuthenticateAsync(source.Id, second.Token, default));

        // Revocation is idempotent so an operator retry cannot fail halfway through rotation.
        Assert.True((await service.RevokeAsync(source.Id, first.Id, default)).IsSuccess);
    }

    [Fact]
    public async Task RefusesReportSourceAndRevokesKeysWhenApiSourceBecomesMailbox()
    {
        await using var db = NewDb();
        var (_, source) = await SeedApiSourceAsync(db);
        var credentials = new ApiSourceCredentialService(db);
        var issued = (await credentials.IssueAsync(source.Id, default)).Value!;

        var mailbox = new ReportSource
        {
            Name = "Mailbox",
            Protocol = "imap",
            Host = "imap.example",
            Port = 993,
            UseTls = true,
            Username = "reports@example",
            PasswordEncrypted = "encrypted",
            DefaultClientId = source.DefaultClientId,
        };
        db.ReportSources.Add(mailbox);
        await db.SaveChangesAsync();
        Assert.False((await credentials.IssueAsync(mailbox.Id, default)).IsSuccess);

        var sourceService = new ReportSourceService(
            db,
            new AesGcmCredentialProtector(Convert.ToBase64String(new byte[32])),
            TestCurrentUserContext.Admin());
        var changed = await sourceService.UpdateAsync(source.Id, new UpdateReportSourceRequest
        {
            Protocol = "imap",
            Host = "imap.example",
            Port = 993,
            UseTls = true,
            Username = "reports@example",
            Password = "secret",
        }, default);

        Assert.True(changed.IsSuccess);
        Assert.NotNull((await db.ApiSourceCredentials.SingleAsync()).RevokedAtUtc);
        Assert.Null(await new ApiSourceAuthenticator(db).AuthenticateAsync(source.Id, issued.Token, default));
    }

    private static async Task<(Client Client, ReportSource Source)> SeedApiSourceAsync(
        DmarcAnalyzerDbContext db)
    {
        var client = new Client { Name = "Acme", Slug = Guid.NewGuid().ToString("N"), Timezone = "UTC" };
        var source = new ReportSource
        {
            Name = "Bifrost upload",
            Protocol = "api",
            DefaultClientId = client.Id,
        };
        db.AddRange(client, source);
        await db.SaveChangesAsync();
        return (client, source);
    }
}
