using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Migrations)]
[Trait("Category", "Migration")]
public sealed class MigrationIntegrationTests(PostgreSqlDatabaseFixture database)
{
    // Keep the release pin separate from the expected latest migration so the
    // upgrade test proves this schema-bearing slice preserves configuration.
    private const string BeforeApiSourceMigration = "20260806191701_AddSmtpTlsReportIngestion";
    private const string PreviousReleaseLatestMigration = "20260811195529_AddApiReportSource";
    private const string ExpectedLatestMigration = "20260812012105_AddServiceApiCredentials";

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

    [Fact]
    public async Task ApiSourceMigration_EnforcesProtocolSpecificConfiguration()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var client = new Client
        {
            Name = "API source fixture",
            Slug = "api-source-fixture",
            Timezone = "UTC",
        };

        await using (var db = database.CreateDbContext())
        {
            db.AddRange(client, new ReportSource
            {
                Name = "Valid API source",
                Protocol = "api",
                Host = null,
                Port = null,
                UseTls = null,
                Username = null,
                PasswordEncrypted = null,
                DefaultClientId = client.Id,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            db.ReportSources.Add(new ReportSource
            {
                Name = "API source with mailbox host",
                Protocol = "api",
                Host = "imap.example",
                Port = null,
                UseTls = null,
                Username = null,
                PasswordEncrypted = null,
                DefaultClientId = client.Id,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = database.CreateDbContext())
        {
            db.ReportSources.Add(new ReportSource
            {
                Name = "Incomplete IMAP source",
                Protocol = "imap",
                Host = null,
                Port = null,
                UseTls = null,
                Username = null,
                PasswordEncrypted = null,
                DefaultClientId = client.Id,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task DownMigration_RefusesWhileApiSourcesExist()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using (var db = database.CreateDbContext())
        {
            var client = new Client { Name = "API source fixture", Slug = "api-down", Timezone = "UTC" };
            db.AddRange(client, new ReportSource
            {
                Name = "API source",
                Protocol = "api",
                Host = null,
                Port = null,
                UseTls = null,
                Username = null,
                PasswordEncrypted = null,
                DefaultClientId = client.Id,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>()
                .MigrateAsync(BeforeApiSourceMigration));
            Assert.Contains("while API sources exist", error.MessageText, StringComparison.Ordinal);
        }

        await using (var verification = database.CreateDbContext())
        {
            Assert.Equal(PreviousReleaseLatestMigration, (await verification.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Equal(1, await verification.ReportSources.CountAsync(x => x.Protocol == "api"));
        }
    }

    [Fact]
    public async Task DownMigration_PreservesReportSourcesWhenNoApiRowsExist()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var sourceId = Guid.NewGuid();
        await using (var db = database.CreateDbContext())
        {
            var client = new Client { Name = "Mailbox fixture", Slug = "mailbox-down", Timezone = "UTC" };
            db.AddRange(client, new ReportSource
            {
                Id = sourceId,
                Name = "Mailbox",
                Protocol = "imap",
                Host = "imap.example",
                Port = 993,
                UseTls = true,
                Username = "reports@example",
                PasswordEncrypted = "test-only",
                DefaultClientId = client.Id,
            });
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync(BeforeApiSourceMigration);
        }

        await using var verification = database.CreateDbContext();
        Assert.Equal(BeforeApiSourceMigration, (await verification.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal("imap.example", (await verification.ReportSources.SingleAsync(x => x.Id == sourceId)).Host);
    }

    [Fact]
    public async Task CredentialMigration_EnforcesShapeAndRefusesDestructiveDownMigration()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using (var db = database.CreateDbContext())
        {
            var client = new Client { Name = "Credential fixture", Slug = "credential-fixture", Timezone = "UTC" };
            var source = new ReportSource
            {
                Name = "API source",
                Protocol = "api",
                UseTls = null,
                DefaultClientId = client.Id,
            };
            db.AddRange(client, source, new ApiSourceCredential
            {
                ReportSourceId = source.Id,
                Prefix = "abcdefghijklmnopqrstuv",
                TokenHash = new byte[32],
            });
            await db.SaveChangesAsync();
        }

        await using (var invalid = database.CreateDbContext())
        {
            var sourceId = await invalid.ReportSources.Select(x => x.Id).SingleAsync();
            invalid.ApiSourceCredentials.Add(new ApiSourceCredential
            {
                ReportSourceId = sourceId,
                Prefix = "too-short",
                TokenHash = new byte[31],
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync());
        }

        await using (var duplicate = database.CreateDbContext())
        {
            var sourceId = await duplicate.ReportSources.Select(x => x.Id).SingleAsync();
            duplicate.ApiSourceCredentials.Add(new ApiSourceCredential
            {
                ReportSourceId = sourceId,
                Prefix = "abcdefghijklmnopqrstuv",
                TokenHash = Enumerable.Repeat((byte)1, 32).ToArray(),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }

        await using (var down = database.CreateDbContext())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => down.GetService<IMigrator>()
                .MigrateAsync(PreviousReleaseLatestMigration));
            Assert.Contains("while credential rows exist", error.MessageText, StringComparison.Ordinal);
        }
    }
}
