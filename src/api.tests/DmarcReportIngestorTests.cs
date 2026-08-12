using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class DmarcReportIngestorTests
{
    [Fact]
    public async Task IngestParsed_RejectsZeroRecordsBeforeDomainResolution()
    {
        await using var db = new DmarcAnalyzerDbContext(
            new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var resolver = new TrackingDomainIngestResolver();
        var ingestor = new DmarcReportIngestor(db, resolver);
        var rangeBegin = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var report = new DmarcReportParseResult(
            "Synthetic reporter",
            "zero-records",
            rangeBegin,
            rangeBegin.AddDays(1),
            "zero.example",
            0,
            [],
            false,
            false,
            [],
            "reject",
            null,
            100,
            "relaxed",
            "relaxed");

        var outcome = await ingestor.IngestParsedAsync(
            new ReportSourceContext(Guid.NewGuid(), Guid.NewGuid()),
            report,
            CancellationToken.None);

        Assert.Equal(DmarcIngestOutcome.Rejected, outcome);
        Assert.False(resolver.WasCalled);
    }

    private sealed class TrackingDomainIngestResolver : IDomainIngestResolver
    {
        public bool WasCalled { get; private set; }

        public Task<DomainIngestResolution> ResolveOrCreateAsync(
            Guid defaultClientId,
            string normalizedDomain,
            CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(new DomainIngestResolution(Guid.NewGuid(), defaultClientId));
        }
    }
}
