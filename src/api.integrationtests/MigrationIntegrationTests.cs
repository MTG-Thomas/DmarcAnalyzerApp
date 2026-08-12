using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Application.Auth;
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
    private const string PreviousReleaseLatestMigration = "20260811195529_AddApiMailboxSource";
    private const string BeforeServicePermissionsMigration = "20260812012105_AddServiceApiCredentials";
    private const string BeforePasskeyMigration = "20260812025139_AddServiceApiCredentialPermissions";
    private const string ExpectedLatestMigration = "20260812033233_AddUserPasskeys";

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
    public async Task ExistingServiceCredential_UpgradesToReadOnlyPermission()
    {
        await database.ResetDatabaseAsync();

        var id = Guid.NewGuid();
        var name = "Bifrost";
        var prefix = "abcdefghijklmnopqrstuv";
        var created = DateTime.UtcNow;
        var expires = created.AddDays(30);
        await using (var previous = database.CreateDbContext())
        {
            await previous.GetService<IMigrator>().MigrateAsync(BeforeServicePermissionsMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO service_api_credential
                    ("Id", "Name", "Prefix", "TokenHash", "CreatedAtUtc", "ExpiresAtUtc")
                VALUES
                    ({id}, {name}, {prefix}, {new byte[32]}, {created}, {expires})
                """);
            await previous.Database.MigrateAsync();
        }

        await using var current = database.CreateDbContext();
        var credential = await current.ServiceApiCredentials.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(["portfolio.read"], credential.Permissions);
    }

    [Fact]
    public async Task PasskeyMigration_EnforcesCredentialShapeUniquenessAndSafeDown()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var user = new AgencyUser
        {
            Email = "passkey-migration@example.test",
            DisplayName = "Passkey migration",
            PasswordHash = "not-used",
            Role = Roles.AgencyAdmin,
        };
        var credentialId = Enumerable.Repeat((byte)7, 32).ToArray();
        await using (var db = database.CreateDbContext())
        {
            db.AddRange(user, new UserPasskey
            {
                UserId = user.Id,
                Name = "Security key",
                CredentialId = credentialId,
                PublicKey = new byte[64],
                UserHandle = user.Id.ToByteArray(),
                SignCount = uint.MaxValue,
                Transports = "usb,internal",
                IsBackupEligible = true,
                IsBackedUp = true,
            });
            await db.SaveChangesAsync();
        }

        await using (var duplicate = database.CreateDbContext())
        {
            duplicate.UserPasskeys.Add(new UserPasskey
            {
                UserId = user.Id,
                Name = "Duplicate",
                CredentialId = credentialId,
                PublicKey = new byte[64],
                UserHandle = user.Id.ToByteArray(),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }

        await using (var invalid = database.CreateDbContext())
        {
            invalid.UserPasskeys.Add(new UserPasskey
            {
                UserId = user.Id,
                Name = "Short ID",
                CredentialId = new byte[15],
                PublicKey = new byte[64],
                UserHandle = user.Id.ToByteArray(),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync());
        }

        foreach (var invalidCredential in new[]
        {
            new UserPasskey
            {
                UserId = user.Id, Name = "Short public key", CredentialId = new byte[32],
                PublicKey = new byte[31], UserHandle = user.Id.ToByteArray(),
            },
            new UserPasskey
            {
                UserId = user.Id, Name = "Wrong handle", CredentialId = Enumerable.Repeat((byte)2, 32).ToArray(),
                PublicKey = new byte[64], UserHandle = new byte[15],
            },
            new UserPasskey
            {
                UserId = user.Id, Name = "Counter overflow", CredentialId = Enumerable.Repeat((byte)3, 32).ToArray(),
                PublicKey = new byte[64], UserHandle = user.Id.ToByteArray(), SignCount = (long)uint.MaxValue + 1,
            },
        })
        {
            await using var invalid = database.CreateDbContext();
            invalid.UserPasskeys.Add(invalidCredential);
            await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync());
        }

        await using (var down = database.CreateDbContext())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => down.GetService<IMigrator>()
                .MigrateAsync(BeforePasskeyMigration));
            Assert.Contains("while passkey rows exist", error.MessageText, StringComparison.Ordinal);
        }

        await using (var verification = database.CreateDbContext())
        {
            Assert.Equal(ExpectedLatestMigration, (await verification.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Equal(uint.MaxValue, (await verification.UserPasskeys.SingleAsync()).SignCount);
        }
    }

    [Fact]
    public async Task PasskeyDownMigrationSucceedsWhenNoCredentialsExist()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforePasskeyMigration);
        }

        await using var verification = database.CreateDbContext();
        Assert.Equal(BeforePasskeyMigration, (await verification.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(await verification.Database.SqlQueryRaw<bool>(
            "SELECT to_regclass('public.user_passkey') IS NOT NULL AS \"Value\"").SingleAsync());
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
            Assert.Equal(
                1,
                await verification.Database
                    .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM mailbox_source WHERE \"Protocol\" = 'api'")
                    .SingleAsync());
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
        Assert.Equal(
            "imap.example",
            await verification.Database
                .SqlQueryRaw<string>("SELECT \"Host\" AS \"Value\" FROM mailbox_source WHERE \"Id\" = {0}", sourceId)
                .SingleAsync());
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
