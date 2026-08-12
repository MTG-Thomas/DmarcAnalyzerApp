using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
public sealed class ServiceApiCredentialIntegrationTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task ConcurrentIssueCreatesTwoUsableCredentials()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        var issued = await Task.WhenAll(
            new ServiceApiCredentialService(firstDb).IssueAsync(
                new CreateServiceApiCredentialRequest("Bifrost A", null), default),
            new ServiceApiCredentialService(secondDb).IssueAsync(
                new CreateServiceApiCredentialRequest("Bifrost B", null), default));

        Assert.All(issued, result => Assert.True(result.IsSuccess));
        var first = issued[0].Value!;
        var second = issued[1].Value!;
        Assert.NotEqual(first.Prefix, second.Prefix);

        await using var verification = database.CreateDbContext();
        Assert.Equal(2, await verification.ServiceApiCredentials.CountAsync());
        var authenticator = new ServiceApiAuthenticator(verification);
        Assert.NotNull(await authenticator.AuthenticateAsync(first.Token, default));
        Assert.NotNull(await authenticator.AuthenticateAsync(second.Token, default));
    }

    [Fact]
    public async Task DatabaseEnforcesTokenShapeAndExpiry()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using (var db = database.CreateDbContext())
        {
            db.ServiceApiCredentials.Add(new ServiceApiCredential
            {
                Name = "Bad hash",
                Prefix = "abcdefghijklmnopqrstuv",
                TokenHash = new byte[31],
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = database.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            db.ServiceApiCredentials.Add(new ServiceApiCredential
            {
                Name = "Bad expiry",
                Prefix = "abcdefghijklmnopqrstuv",
                TokenHash = new byte[32],
                CreatedAtUtc = now,
                ExpiresAtUtc = now,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
