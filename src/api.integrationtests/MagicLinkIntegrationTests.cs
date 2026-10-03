using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.MagicLinks;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
public sealed class MagicLinkIntegrationTests(PostgreSqlDatabaseFixture database)
{
    private sealed class MagicLinkUser(Guid clientId) : ICurrentUserContext
    {
        public bool IsAuthenticated => true;
        public string ActorType => "magic_link";
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "magic-link:test";
        public string Role => Roles.ClientViewer;
        public bool IsAdmin => false;
        public bool IsAgencyStaff => false;
        public bool IsService => false;
        public IReadOnlyCollection<string> ServicePermissions => [];
        public bool IsMagicLink => true;
        public Guid? MagicLinkId { get; } = Guid.NewGuid();
        public IReadOnlyCollection<Guid> AllowedClientIds { get; } = [clientId];
        public bool CanAccessClient(Guid id) => id == clientId;
        public bool HasServicePermission(string permission) => false;
    }

    [Fact]
    public async Task MigrationCreatesMagicLinkTableWithConstraints()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using var db = database.CreateDbContext();
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var issued = (await new MagicLinkService(db).IssueAsync(
            new CreateMagicLinkRequest(client.Id, 7, "Review"), default)).Value!;
        var principal = await new MagicLinkAuthenticator(db).AuthenticateAsync(issued.Token, default);
        Assert.NotNull(principal);
        Assert.Equal(client.Id, principal.ClientId);

        // Bad shapes are refused by the database, not just the service.
        await using (var badDb = database.CreateDbContext())
        {
            badDb.MagicLinks.Add(new MagicLink
            {
                ClientId = client.Id,
                Label = "Bad hash",
                Prefix = "abcdefghijklmnopqrstuv",
                TokenHash = new byte[31],
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => badDb.SaveChangesAsync());
        }

        await using (var badDb = database.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            badDb.MagicLinks.Add(new MagicLink
            {
                ClientId = client.Id,
                Label = "Bad expiry",
                Prefix = "abcdefghijklmnopqrstuw",
                TokenHash = new byte[32],
                CreatedAtUtc = now,
                ExpiresAtUtc = now,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => badDb.SaveChangesAsync());
        }

        await using (var badDb = database.CreateDbContext())
        {
            badDb.MagicLinks.Add(new MagicLink
            {
                ClientId = client.Id,
                Label = "Duplicate prefix",
                Prefix = issued.Prefix,
                TokenHash = new byte[32],
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => badDb.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task ConcurrentIssueCreatesTwoUsableLinks()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        Guid clientId;
        await using (var seedDb = database.CreateDbContext())
        {
            var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
            seedDb.Clients.Add(client);
            await seedDb.SaveChangesAsync();
            clientId = client.Id;
        }

        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        var issued = await Task.WhenAll(
            new MagicLinkService(firstDb).IssueAsync(
                new CreateMagicLinkRequest(clientId, 7, "First"), default),
            new MagicLinkService(secondDb).IssueAsync(
                new CreateMagicLinkRequest(clientId, 7, "Second"), default));

        Assert.All(issued, result => Assert.True(result.IsSuccess));
        Assert.NotEqual(issued[0].Value!.Prefix, issued[1].Value!.Prefix);

        await using var verification = database.CreateDbContext();
        Assert.Equal(2, await verification.MagicLinks.CountAsync());
        var authenticator = new MagicLinkAuthenticator(verification);
        Assert.NotNull(await authenticator.AuthenticateAsync(issued[0].Value!.Token, default));
        Assert.NotNull(await authenticator.AuthenticateAsync(issued[1].Value!.Token, default));
    }

    [Fact]
    public async Task MagicLinkScopeSeesOnlyItsOwnClient()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        Guid firstId;
        Guid secondId;
        Guid firstDomainId;
        Guid secondDomainId;
        await using (var seedDb = database.CreateDbContext())
        {
            var first = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
            var second = new Client { Name = "Globex", Slug = "globex", Timezone = "UTC" };
            seedDb.Clients.AddRange(first, second);
            var firstDomain = new Domain { ClientId = first.Id, Name = "acme.example" };
            var secondDomain = new Domain { ClientId = second.Id, Name = "globex.example" };
            seedDb.Domains.AddRange(firstDomain, secondDomain);
            await seedDb.SaveChangesAsync();
            firstId = first.Id;
            secondId = second.Id;
            firstDomainId = firstDomain.Id;
            secondDomainId = secondDomain.Id;
        }

        await using var db = database.CreateDbContext();
        var issued = (await new MagicLinkService(db).IssueAsync(
            new CreateMagicLinkRequest(firstId, 7, "Review"), default)).Value!;
        var principal = await new MagicLinkAuthenticator(db).AuthenticateAsync(issued.Token, default);
        Assert.NotNull(principal);

        // The authenticated link resolves to exactly its own client; every
        // client-scoped read below goes through that single grant.
        var scoped = new MagicLinkUser(principal.ClientId);
        var clients = new ClientService(db, scoped);
        var listed = await clients.ListAsync(default);
        Assert.Equal([firstId], listed.Select(x => x.Id).ToArray());
        Assert.NotNull(await clients.GetAsync(firstId, default));
        Assert.Null(await clients.GetAsync(secondId, default));

        var domains = new DomainService(db, scoped);
        Assert.NotNull(await domains.GetAsync(firstDomainId, default));
        Assert.Null(await domains.GetAsync(secondDomainId, default));
        Assert.All(await domains.ListAsync(null, default), d => Assert.Equal(firstId, d.ClientId));
    }

    [Fact]
    public async Task RevokedAndExpiredLinksCannotAuthenticate()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using var db = database.CreateDbContext();
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var service = new MagicLinkService(db);
        var revoked = (await service.IssueAsync(
            new CreateMagicLinkRequest(client.Id, 7, "Revoked"), default)).Value!;
        var live = (await service.IssueAsync(
            new CreateMagicLinkRequest(client.Id, 7, "Live"), default)).Value!;

        await service.RevokeAsync(revoked.Id, default);

        var authenticator = new MagicLinkAuthenticator(db);
        Assert.Null(await authenticator.AuthenticateAsync(revoked.Token, default));
        Assert.NotNull(await authenticator.AuthenticateAsync(live.Token, default));

        var stored = await db.MagicLinks.SingleAsync(x => x.Id == live.Id);
        stored.CreatedAtUtc = DateTime.UtcNow.AddDays(-8);
        stored.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.Null(await authenticator.AuthenticateAsync(live.Token, default));
    }
}
