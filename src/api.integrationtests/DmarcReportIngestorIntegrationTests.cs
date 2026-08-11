using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class DmarcReportIngestorIntegrationTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task IngestParsed_PersistsFullReportGraphAndLedger()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var report = CreateReport("full-graph", "new.example", recordCount: 2);

        await using var db = database.CreateDbContext();
        var outcome = await CreateIngestor(db).IngestParsedAsync(seeded.Source, report, CancellationToken.None);

        Assert.Equal(DmarcIngestOutcome.Inserted, outcome);

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReports.CountAsync());
        Assert.Equal(2, await verification.DmarcReportRecords.CountAsync());
        Assert.Equal(2, await verification.DmarcReportRecordDkimAuthResults.CountAsync());
        Assert.Equal(2, await verification.DmarcReportRecordSpfAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task IngestParsed_ChildFailureRollsBackReportGraphAndLedger()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var report = CreateReport(
            "rollback",
            "rollback.example",
            dkimSelector: new string('x', 256));

        await using var db = database.CreateDbContext();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateIngestor(db).IngestParsedAsync(seeded.Source, report, CancellationToken.None));

        await using var verification = database.CreateDbContext();
        Assert.Equal(0, await verification.DmarcReports.CountAsync());
        Assert.Equal(0, await verification.DmarcReportRecords.CountAsync());
        Assert.Equal(0, await verification.DmarcReportRecordDkimAuthResults.CountAsync());
        Assert.Equal(0, await verification.DmarcReportRecordSpfAuthResults.CountAsync());
        Assert.Equal(0, await verification.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task IngestParsed_ExactReplayCreatesNoChildRows()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var report = CreateReport("replay", "replay.example");

        await using var db = database.CreateDbContext();
        var ingestor = CreateIngestor(db);

        Assert.Equal(
            DmarcIngestOutcome.Inserted,
            await ingestor.IngestParsedAsync(seeded.Source, report, CancellationToken.None));
        Assert.Equal(
            DmarcIngestOutcome.Duplicate,
            await ingestor.IngestParsedAsync(seeded.Source, report, CancellationToken.None));

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReports.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecords.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecordDkimAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecordSpfAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task IngestParsed_ConcurrentReplayCreatesOneCompleteGraph()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var report = CreateReport("concurrent", "concurrent.example");
        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        using var barrier = new Barrier(3);

        var first = StartAtBarrierAsync(CreateIngestor(firstDb), seeded.Source, report, barrier);
        var second = StartAtBarrierAsync(CreateIngestor(secondDb), seeded.Source, report, barrier);
        barrier.SignalAndWait();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, x => x == DmarcIngestOutcome.Inserted);
        Assert.Single(outcomes, x => x == DmarcIngestOutcome.Duplicate);

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReports.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecords.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecordDkimAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecordSpfAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task IngestParsed_ExistingDomainOwnerOverridesSourceDefault()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var owner = new Client { Name = "Existing owner", Slug = $"owner-{Guid.NewGuid():N}", Timezone = "UTC" };
        var domain = new Domain { ClientId = owner.Id, Name = "owned.example" };

        await using (var seedDb = database.CreateDbContext())
        {
            seedDb.AddRange(owner, domain);
            await seedDb.SaveChangesAsync();
        }

        await using var db = database.CreateDbContext();
        var outcome = await CreateIngestor(db).IngestParsedAsync(
            seeded.Source, CreateReport("owned", domain.Name), CancellationToken.None);

        Assert.Equal(DmarcIngestOutcome.Inserted, outcome);

        await using var verification = database.CreateDbContext();
        Assert.Equal(owner.Id, await verification.Domains.Where(x => x.Id == domain.Id).Select(x => x.ClientId).SingleAsync());
        Assert.Equal(domain.Id, await verification.DmarcReports.Select(x => x.DomainId).SingleAsync());
        Assert.Equal(owner.Id, await verification.DmarcReportIngests.Select(x => x.ClientId).SingleAsync());
    }

    [Fact]
    public async Task IngestParsed_NewDomainUsesSourceDefaultClient()
    {
        var seeded = await ResetMigrateAndSeedAsync();

        await using var db = database.CreateDbContext();
        var outcome = await CreateIngestor(db).IngestParsedAsync(
            seeded.Source, CreateReport("new-owner", "new-owner.example"), CancellationToken.None);

        Assert.Equal(DmarcIngestOutcome.Inserted, outcome);

        await using var verification = database.CreateDbContext();
        Assert.Equal(
            seeded.DefaultClientId,
            await verification.Domains.Where(x => x.Name == "new-owner.example").Select(x => x.ClientId).SingleAsync());
        Assert.Equal(seeded.DefaultClientId, await verification.DmarcReportIngests.Select(x => x.ClientId).SingleAsync());
    }

    [Fact]
    public async Task IngestParsed_RejectsInvalidParsedContractWithoutWrites()
    {
        var seeded = await ResetMigrateAndSeedAsync();

        await using var db = database.CreateDbContext();
        var outcome = await CreateIngestor(db).IngestParsedAsync(
            seeded.Source, CreateReport("rejected", " "), CancellationToken.None);

        Assert.Equal(DmarcIngestOutcome.Rejected, outcome);

        await using var verification = database.CreateDbContext();
        Assert.Equal(0, await verification.Domains.CountAsync());
        Assert.Equal(0, await verification.DmarcReports.CountAsync());
        Assert.Equal(0, await verification.DmarcReportIngests.CountAsync());
    }

    private static Task<DmarcIngestOutcome> StartAtBarrierAsync(
        IDmarcReportIngestor ingestor,
        ReportSourceContext source,
        DmarcReportParseResult report,
        Barrier barrier)
        => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await ingestor.IngestParsedAsync(source, report, CancellationToken.None);
        });

    private static DmarcReportIngestor CreateIngestor(DmarcAnalyzer.Api.Data.DmarcAnalyzerDbContext db)
        => new(db, new DomainIngestResolver(db));

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
        var source = new MailboxSource
        {
            Name = "Synthetic integration source",
            Protocol = "imap",
            Host = "imap.example",
            Port = 993,
            Username = "reports@example",
            PasswordEncrypted = "test-only-not-a-secret",
            DefaultClientId = client.Id,
        };

        await using var db = database.CreateDbContext();
        db.AddRange(client, source);
        await db.SaveChangesAsync();

        return new SeededIds(client.Id, new ReportSourceContext(source.Id, client.Id));
    }

    private static DmarcReportParseResult CreateReport(
        string reportId,
        string policyDomain,
        int recordCount = 1,
        string dkimSelector = "selector")
    {
        var records = Enumerable.Range(1, recordCount)
            .Select(index => new DmarcReportRecordParseResult(
                $"192.0.2.{index}",
                index,
                "none",
                "pass",
                "pass",
                policyDomain,
                $"sender-{index}.example",
                $"recipient-{index}.example",
                [new DmarcReportRecordDkimAuthParseResult(policyDomain, dkimSelector, "pass", string.Empty)],
                [new DmarcReportRecordSpfAuthParseResult(policyDomain, "mfrom", "pass", string.Empty)]))
            .ToArray();

        var rangeBegin = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        return new DmarcReportParseResult(
            "Synthetic reporter",
            reportId,
            rangeBegin,
            rangeBegin.AddDays(1),
            policyDomain,
            records.Length,
            records,
            false,
            false,
            [],
            "reject",
            null,
            100,
            "relaxed",
            "relaxed");
    }

    private sealed record SeededIds(Guid DefaultClientId, ReportSourceContext Source);
}
