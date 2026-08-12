using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class AnalyticsDispositionTests
{
    private const string SourceIp = "192.0.2.10";

    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static Domain SeedReport(DmarcAnalyzerDbContext db)
    {
        var now = DateTime.UtcNow;
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Example client",
            Slug = "example-client",
            Timezone = "UTC",
            RetentionMonths = 27,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = "analytics.example",
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(),
            DomainId = domain.Id,
            ReportSourceId = Guid.NewGuid(),
            OrganizationName = "reporter.example",
            ReportId = "rfc9990-analytics",
            RangeBeginUtc = now.AddHours(-2),
            RangeEndUtc = now.AddHours(-1),
            RecordCount = 4,
            IngestedAtUtc = now,
            PublishedPolicy = "reject",
            PublishedPct = 100,
        };

        db.AddRange(client, domain, report);
        AddRecord(db, report, "none", 3, "fail", "pass");
        AddRecord(db, report, "pass", 7, "pass", "fail");
        AddRecord(db, report, "quarantine", 5, "fail", "fail");
        AddRecord(db, report, "reject", 2, "fail", "fail");
        return domain;
    }

    private static void AddRecord(
        DmarcAnalyzerDbContext db,
        DmarcReport report,
        string disposition,
        int messageCount,
        string dkim,
        string spf)
    {
        db.Add(new DmarcReportRecord
        {
            Id = Guid.NewGuid(),
            DmarcReportId = report.Id,
            ReportRangeBeginUtc = report.RangeBeginUtc,
            SourceIp = SourceIp,
            MessageCount = messageCount,
            Disposition = disposition,
            DkimResult = dkim,
            SpfResult = spf,
            HeaderFrom = "analytics.example",
            EnvelopeFrom = "sender.example",
        });
    }

    [Fact]
    public async Task SummaryIncludesPassWithoutChangingComplianceOrBlockedBuckets()
    {
        await using var db = NewDb();
        SeedReport(db);
        await db.SaveChangesAsync();

        var summary = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .GetSummaryAsync(30, CancellationToken.None);

        Assert.Equal(3, summary.Dispositions.None);
        Assert.Equal(7, summary.Dispositions.Pass);
        Assert.Equal(5, summary.Dispositions.Quarantine);
        Assert.Equal(2, summary.Dispositions.Reject);
        Assert.Equal(
            summary.Totals.Messages,
            summary.Dispositions.None
            + summary.Dispositions.Pass
            + summary.Dispositions.Quarantine
            + summary.Dispositions.Reject);

        // Compliance remains an aligned DKIM/SPF result, not an action disposition.
        Assert.Equal(17, summary.Totals.Messages);
        Assert.Equal(10, summary.Totals.CompliantMessages);

        // The blocked total remains quarantine + reject; RFC 9990 pass is not blocked.
        Assert.Equal(7, summary.Dispositions.Quarantine + summary.Dispositions.Reject);
    }

    [Fact]
    public async Task SourceDetailIncludesPassAndEveryDispositionSumsToMessages()
    {
        await using var db = NewDb();
        var domain = SeedReport(db);
        await db.SaveChangesAsync();

        var detail = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .GetSourceDetailAsync(domain.Id, SourceIp, 30, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(3, detail.Dispositions.None);
        Assert.Equal(7, detail.Dispositions.Pass);
        Assert.Equal(5, detail.Dispositions.Quarantine);
        Assert.Equal(2, detail.Dispositions.Reject);
        Assert.Equal(
            detail.Messages,
            detail.Dispositions.None
            + detail.Dispositions.Pass
            + detail.Dispositions.Quarantine
            + detail.Dispositions.Reject);
        Assert.Equal(10, detail.CompliantMessages);
    }

}
