using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
public sealed class ApiSourceCredentialIntegrationTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task ConcurrentRotationCreatesTwoUsableOverlappingCredentials()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var client = new Client { Name = "API client", Slug = "api-client", Timezone = "UTC" };
        var source = new ReportSource
        {
            Name = "Bifrost upload",
            Protocol = "api",
            UseTls = null,
            DefaultClientId = client.Id,
        };
        await using (var seed = database.CreateDbContext())
        {
            seed.AddRange(client, source);
            await seed.SaveChangesAsync();
        }

        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        var issued = await Task.WhenAll(
            new ApiSourceCredentialService(firstDb).IssueAsync(source.Id, default),
            new ApiSourceCredentialService(secondDb).IssueAsync(source.Id, default));
        var first = issued[0];
        var second = issued[1];

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value!.Prefix, second.Value!.Prefix);

        await using var verification = database.CreateDbContext();
        Assert.Equal(2, await verification.ApiSourceCredentials.CountAsync());
        var authenticator = new ApiSourceAuthenticator(verification);
        Assert.NotNull(await authenticator.AuthenticateAsync(
            source.Id,
            first.Value.Token,
            default));
        Assert.NotNull(await authenticator.AuthenticateAsync(
            source.Id,
            second.Value.Token,
            default));
    }

    [Fact]
    public async Task IssueLosingRaceToProtocolChangeCannotPersist()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();
        var sourceId = await SeedApiSourceAsync("issue-loses");

        await using var transitionDb = database.CreateDbContext();
        await using var transition = await transitionDb.Database.BeginTransactionAsync();
        var source = await transitionDb.ReportSources.SingleAsync(x => x.Id == sourceId);
        MakeMailbox(source);
        await transitionDb.SaveChangesAsync();

        await using var issueDb = database.CreateDbContext();
        var issue = new ApiSourceCredentialService(issueDb).IssueAsync(sourceId, default);
        Assert.NotSame(issue, await Task.WhenAny(issue, Task.Delay(TimeSpan.FromMilliseconds(250))));

        await transition.CommitAsync();
        var result = await issue;
        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);

        await using var verification = database.CreateDbContext();
        Assert.Equal("imap", (await verification.ReportSources.SingleAsync()).Protocol);
        Assert.Empty(await verification.ApiSourceCredentials.ToListAsync());
    }

    [Fact]
    public async Task ProtocolChangeLosingRaceToIssueRevokesTheNewCredential()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();
        var sourceId = await SeedApiSourceAsync("transition-loses");

        await using var issueDb = database.CreateDbContext();
        await using var issue = await issueDb.Database.BeginTransactionAsync();
        issueDb.ApiSourceCredentials.Add(new ApiSourceCredential
        {
            ReportSourceId = sourceId,
            Prefix = "abcdefghijklmnopqrstuv",
            TokenHash = new byte[32],
        });
        await issueDb.SaveChangesAsync();

        await using var transitionDb = database.CreateDbContext();
        var source = await transitionDb.ReportSources.SingleAsync(x => x.Id == sourceId);
        MakeMailbox(source);
        var transition = transitionDb.SaveChangesAsync();
        Assert.NotSame(transition, await Task.WhenAny(
            transition,
            Task.Delay(TimeSpan.FromMilliseconds(250))));

        await issue.CommitAsync();
        await transition;

        await using var verification = database.CreateDbContext();
        Assert.Equal("imap", (await verification.ReportSources.SingleAsync()).Protocol);
        Assert.NotNull((await verification.ApiSourceCredentials.SingleAsync()).RevokedAtUtc);
    }

    private async Task<Guid> SeedApiSourceAsync(string slug)
    {
        var client = new Client { Name = slug, Slug = slug, Timezone = "UTC" };
        var source = new ReportSource
        {
            Name = "Bifrost upload",
            Protocol = "api",
            UseTls = null,
            DefaultClientId = client.Id,
        };
        await using var seed = database.CreateDbContext();
        seed.AddRange(client, source);
        await seed.SaveChangesAsync();
        return source.Id;
    }

    private static void MakeMailbox(ReportSource source)
    {
        source.Protocol = "imap";
        source.Host = "imap.example";
        source.Port = 993;
        source.UseTls = true;
        source.Username = "reports@example";
        source.PasswordEncrypted = "test-only";
    }
}
