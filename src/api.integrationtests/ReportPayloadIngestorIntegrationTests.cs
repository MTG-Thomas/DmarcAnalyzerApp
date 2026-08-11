using System.IO.Compression;
using System.Text;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class ReportPayloadIngestorIntegrationTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task DmarcCrossContainerReplayCreatesOneCompleteGraph()
    {
        var source = await ResetMigrateAndSeedAsync();
        var xml = Fixture("sample-yahoo-aggregate.xml");

        await using var db = database.CreateDbContext();
        var ingestor = CreateIngestor(db);
        await using var bare = new MemoryStream(xml, writable: false);
        var inserted = await ingestor.IngestAsync(
            source, bare, new("report.xml"), CancellationToken.None);

        await using var gzip = new MemoryStream(Gzip(xml), writable: false);
        var duplicate = await ingestor.IngestAsync(
            source, gzip, new("report.xml.gz"), CancellationToken.None);

        Assert.Equal(1, inserted.DmarcInserted);
        Assert.Equal(1, duplicate.DmarcDuplicates);

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReports.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecords.CountAsync());
        Assert.Equal(2, await verification.DmarcReportRecordDkimAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecordSpfAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task DmarcChildFailureRollsBackAndCorrectedReplayCanInsert()
    {
        var source = await ResetMigrateAndSeedAsync();
        var valid = Encoding.UTF8.GetString(Fixture("sample-yahoo-aggregate.xml"));
        var invalid = Encoding.UTF8.GetBytes(valid.Replace(
            "<selector>k1</selector>",
            $"<selector>{new string('x', 256)}</selector>",
            StringComparison.Ordinal));

        await using var db = database.CreateDbContext();
        var ingestor = CreateIngestor(db);
        await using var brokenPayload = new MemoryStream(invalid, writable: false);
        await Assert.ThrowsAnyAsync<Exception>(() => ingestor.IngestAsync(
            source, brokenPayload, new("report.xml"), CancellationToken.None));

        await using (var failedState = database.CreateDbContext())
        {
            Assert.Equal(0, await failedState.DmarcReports.CountAsync());
            Assert.Equal(0, await failedState.DmarcReportRecords.CountAsync());
            Assert.Equal(0, await failedState.DmarcReportIngests.CountAsync());
        }

        await using var correctedPayload = new MemoryStream(Encoding.UTF8.GetBytes(valid), writable: false);
        var corrected = await ingestor.IngestAsync(
            source, correctedPayload, new("report.xml"), CancellationToken.None);

        Assert.Equal(1, corrected.DmarcInserted);
        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReports.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecords.CountAsync());
        Assert.Equal(1, await verification.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task TlsReplayThroughSourceContextCreatesOneCompleteGraph()
    {
        var source = await ResetMigrateAndSeedAsync();
        var json = Fixture("sample-rfc8460-tls.json");

        await using var db = database.CreateDbContext();
        var ingestor = CreateIngestor(db);
        await using var bare = new MemoryStream(json, writable: false);
        var inserted = await ingestor.IngestAsync(
            source, bare, new("report.json"), CancellationToken.None);

        await using var gzip = new MemoryStream(Gzip(json), writable: false);
        var duplicate = await ingestor.IngestAsync(
            source, gzip, new("report.json.gz"), CancellationToken.None);

        Assert.Equal(1, inserted.TlsInserted);
        Assert.Equal(1, duplicate.TlsDuplicates);

        await using var verification = database.CreateDbContext();
        var persistedReport = await verification.SmtpTlsReports.SingleAsync();
        var persistedLedger = await verification.TlsReportIngests.SingleAsync();
        Assert.Equal(source.SourceId, persistedReport.MailboxSourceId);
        Assert.Equal(source.SourceId, persistedLedger.MailboxSourceId);
        Assert.Equal(source.DefaultClientId, persistedLedger.ClientId);
        Assert.Equal(1, await verification.SmtpTlsReportPolicies.CountAsync());
        Assert.Equal(3, await verification.SmtpTlsFailureDetails.CountAsync());
    }

    private async Task<ReportSourceContext> ResetMigrateAndSeedAsync()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var client = new Client
        {
            Name = "Raw payload fixture",
            Slug = $"raw-payload-{Guid.NewGuid():N}",
            Timezone = "UTC",
        };
        var source = new MailboxSource
        {
            Name = "Synthetic raw source",
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
        return new(source.Id, client.Id);
    }

    private static ReportPayloadIngestor CreateIngestor(DmarcAnalyzerDbContext db)
    {
        var resolver = new DomainIngestResolver(db);
        return new(
            new BoundedReportPayloadExtractor(Options.Create(new ReportPayloadExtractionOptions())),
            new DmarcRuaReportParser(),
            new DmarcReportIngestor(db, resolver),
            new TlsRptReportParser(),
            new TlsReportIngestor(db, resolver));
    }

    private static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }
}
