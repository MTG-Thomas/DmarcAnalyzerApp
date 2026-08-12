using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class PersistenceConstraintIntegrationTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task DomainResolver_ConcurrentCreateReturnsOneDatabaseIdentity()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        var firstResolver = new DomainIngestResolver(firstDb);
        var secondResolver = new DomainIngestResolver(secondDb);

        var results = await Task.WhenAll(
            firstResolver.ResolveOrCreateAsync(seeded.ClientId, "resolver.example", CancellationToken.None),
            secondResolver.ResolveOrCreateAsync(seeded.ClientId, "resolver.example", CancellationToken.None));

        Assert.Equal(results[0], results[1]);

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.Domains.CountAsync(x => x.Name == "resolver.example"));
    }

    [Fact]
    public async Task DmarcReportUniqueIndex_RejectsOneOfTwoConcurrentBusinessKeys()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var rangeBegin = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var rangeEnd = rangeBegin.AddDays(1);

        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();

        var attempts = await Task.WhenAll(
            TryInsertReportAsync(firstDb, seeded, rangeBegin, rangeEnd),
            TryInsertReportAsync(secondDb, seeded, rangeBegin, rangeEnd));

        Assert.Single(attempts, exception => exception is null);
        Assert.Single(attempts, exception => exception is DbUpdateException);

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReports.CountAsync());
    }

    [Fact]
    public async Task DmarcIngestLedgerUniqueIndex_RejectsOneOfTwoConcurrentBusinessKeys()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var rangeBegin = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var rangeEnd = rangeBegin.AddDays(1);

        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();

        var attempts = await Task.WhenAll(
            TryInsertLedgerAsync(firstDb, seeded, rangeBegin, rangeEnd),
            TryInsertLedgerAsync(secondDb, seeded, rangeBegin, rangeEnd));

        Assert.Single(attempts, exception => exception is null);
        Assert.Single(attempts, exception => exception is DbUpdateException);

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReportIngests.CountAsync());
    }

    private async Task<SeededIds> ResetMigrateAndSeedAsync()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var client = new Client
        {
            Name = "Persistence fixture",
            Slug = $"persistence-{Guid.NewGuid():N}",
            Timezone = "UTC",
        };
        var source = new ReportSource
        {
            Name = "Synthetic integration source",
            Protocol = "imap",
            Host = "imap.example",
            Port = 993,
            Username = "reports@example",
            PasswordEncrypted = "test-only-not-a-secret",
            DefaultClientId = client.Id,
        };
        var domain = new Domain
        {
            ClientId = client.Id,
            Name = "persistence.example",
        };

        await using var db = database.CreateDbContext();
        db.AddRange(client, source, domain);
        await db.SaveChangesAsync();

        return new SeededIds(client.Id, source.Id, domain.Id);
    }

    private static async Task<Exception?> TryInsertReportAsync(
        DmarcAnalyzer.Api.Data.DmarcAnalyzerDbContext db,
        SeededIds seeded,
        DateTime rangeBegin,
        DateTime rangeEnd)
    {
        db.DmarcReports.Add(new DmarcReport
        {
            DomainId = seeded.DomainId,
            ReportSourceId = seeded.SourceId,
            OrganizationName = "Reporter",
            ReportId = "concurrent-report",
            RangeBeginUtc = rangeBegin,
            RangeEndUtc = rangeEnd,
            RecordCount = 1,
        });

        return await CaptureDbUpdateExceptionAsync(() => db.SaveChangesAsync());
    }

    private static async Task<Exception?> TryInsertLedgerAsync(
        DmarcAnalyzer.Api.Data.DmarcAnalyzerDbContext db,
        SeededIds seeded,
        DateTime rangeBegin,
        DateTime rangeEnd)
    {
        db.DmarcReportIngests.Add(new DmarcReportIngest
        {
            ClientId = seeded.ClientId,
            ReportSourceId = seeded.SourceId,
            PolicyDomain = "persistence.example",
            ReportId = "concurrent-ledger",
            ReportRangeBeginUtc = rangeBegin,
            ReportRangeEndUtc = rangeEnd,
            OrganizationName = "Reporter",
            RecordCount = 1,
        });

        return await CaptureDbUpdateExceptionAsync(() => db.SaveChangesAsync());
    }

    private static async Task<Exception?> CaptureDbUpdateExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (DbUpdateException exception)
        {
            return exception;
        }
    }

    private sealed record SeededIds(Guid ClientId, Guid SourceId, Guid DomainId);
}
