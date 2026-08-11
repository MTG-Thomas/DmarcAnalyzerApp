using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Migrations)]
[Trait("Category", "Migration")]
public sealed class MigrationIntegrationTests(PostgreSqlDatabaseFixture database)
{
    // v0.9.0 and v0.10.0 contain the same migration set. Keeping the release
    // pin separate makes that no-op upgrade contract explicit and gives the
    // next schema-bearing release one place to advance it.
    private const string PreviousReleaseLatestMigration = "20260806191701_AddSmtpTlsReportIngestion";
    private const string ExpectedLatestMigration = "20260806191701_AddSmtpTlsReportIngestion";

    [Fact]
    public async Task EmptyDatabase_MigratesToPinnedLatestSchema()
    {
        await database.ResetDatabaseAsync();

        await using var db = database.CreateDbContext();
        Assert.Equal(ExpectedLatestMigration, db.Database.GetMigrations().Last());

        await db.Database.MigrateAsync();

        Assert.Equal(ExpectedLatestMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task PreviousReleaseSchema_UpgradesWithoutChangingSeededConfiguration()
    {
        await database.ResetDatabaseAsync();

        var clientId = Guid.NewGuid();
        await using (var previousRelease = database.CreateDbContext())
        {
            await previousRelease.GetService<IMigrator>()
                .MigrateAsync(PreviousReleaseLatestMigration);

            previousRelease.Clients.Add(new Client
            {
                Id = clientId,
                Name = "Migration fixture",
                Slug = "migration-fixture",
                Timezone = "America/Indianapolis",
            });
            await previousRelease.SaveChangesAsync();
        }

        await using (var current = database.CreateDbContext())
        {
            await current.Database.MigrateAsync();

            var client = await current.Clients.AsNoTracking().SingleAsync(x => x.Id == clientId);
            Assert.Equal("Migration fixture", client.Name);
            Assert.Equal("America/Indianapolis", client.Timezone);
            Assert.Equal(ExpectedLatestMigration, (await current.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Empty(await current.Database.GetPendingMigrationsAsync());
        }
    }
}
