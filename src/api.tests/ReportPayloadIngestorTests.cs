using System.IO.Compression;
using System.Security.Cryptography;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ReportPayloadIngestorTests
{
    private static readonly ReportSourceContext Source = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task MixedZipRoutesBothFormatsAndPreservesJunkRejection()
    {
        var dmarc = new StubDmarcIngestor(_ => DmarcIngestOutcome.Inserted);
        var tls = new StubTlsIngestor(_ => TlsReportIngestOutcome.Inserted);
        var ingestor = CreateIngestor(dmarc, tls);
        var zip = Zip(
            ("00-readme.txt", "not a report"u8.ToArray()),
            ("01-report.xml", Fixture("sample-yahoo-aggregate.xml")),
            ("02-report.json", Fixture("sample-rfc8460-tls.json")));

        await using var payload = new MemoryStream(zip, writable: false);
        var result = await ingestor.IngestAsync(
            Source, payload, new("mislabelled.gz", "application/gzip"), CancellationToken.None);

        Assert.Equal(1, result.DmarcInserted);
        Assert.Equal(1, result.TlsInserted);
        Assert.Equal(2, result.ReportsProcessed);
        Assert.Single(dmarc.Reports);
        Assert.Single(tls.Reports);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(ReportPayloadRejectionCode.UnsupportedFormat, rejection.Code);
        Assert.Equal("00-readme.txt", rejection.SourceName);
    }

    [Fact]
    public async Task InvalidSiblingIsTypedAndDoesNotBlockValidReport()
    {
        var dmarc = new StubDmarcIngestor(_ => DmarcIngestOutcome.Inserted);
        var tls = new StubTlsIngestor(_ => TlsReportIngestOutcome.Inserted);
        var ingestor = CreateIngestor(dmarc, tls);
        var zip = Zip(
            ("broken.xml", "<feedback>"u8.ToArray()),
            ("report.json", Fixture("sample-rfc8460-tls.json")));

        await using var payload = new MemoryStream(zip, writable: false);
        var result = await ingestor.IngestAsync(
            Source, payload, new("reports.zip"), CancellationToken.None);

        Assert.Equal(1, result.DmarcRejected);
        Assert.Equal(1, result.TlsInserted);
        Assert.Empty(dmarc.Reports);
        Assert.Single(tls.Reports);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(ReportPayloadRejectionCode.InvalidDmarcReport, rejection.Code);
        Assert.Equal("broken.xml", rejection.SourceName);
    }

    [Fact]
    public async Task CrossContainerReplayMapsParsedPersistenceOutcomes()
    {
        var seen = false;
        var dmarc = new StubDmarcIngestor(_ =>
        {
            var outcome = seen ? DmarcIngestOutcome.Duplicate : DmarcIngestOutcome.Inserted;
            seen = true;
            return outcome;
        });
        var ingestor = CreateIngestor(dmarc, new StubTlsIngestor(_ => TlsReportIngestOutcome.Inserted));
        var xml = Fixture("sample-yahoo-aggregate.xml");

        await using var bare = new MemoryStream(xml, writable: false);
        var inserted = await ingestor.IngestAsync(
            Source, bare, new("report.xml"), CancellationToken.None);

        await using var gzip = new MemoryStream(Gzip(xml), writable: false);
        var duplicate = await ingestor.IngestAsync(
            Source, gzip, new("report.xml.gz"), CancellationToken.None);

        Assert.Equal(1, inserted.DmarcInserted);
        Assert.Equal(1, duplicate.DmarcDuplicates);
        Assert.Equal(ReportPayloadContainer.Bare, inserted.Container);
        Assert.Equal(ReportPayloadContainer.Gzip, duplicate.Container);
        Assert.Equal(2, dmarc.Reports.Count);
    }

    [Fact]
    public async Task RequestLimitRejectsBeforeAnyParserOrPersistenceCall()
    {
        var dmarc = new StubDmarcIngestor(_ => DmarcIngestOutcome.Inserted);
        var tls = new StubTlsIngestor(_ => TlsReportIngestOutcome.Inserted);
        var limits = new ReportPayloadExtractionOptions { MaxRequestBytes = 8 };
        var ingestor = CreateIngestor(dmarc, tls, limits);

        await using var payload = new MemoryStream(Fixture("sample-yahoo-aggregate.xml"), writable: false);
        var result = await ingestor.IngestAsync(
            Source, payload, new("report.xml"), CancellationToken.None);

        Assert.Equal(ReportPayloadRejectionCode.RequestTooLarge, Assert.Single(result.Rejections).Code);
        Assert.Equal(0, result.ReportsProcessed);
        Assert.Empty(dmarc.Reports);
        Assert.Empty(tls.Reports);
    }

    [Fact]
    public async Task ExpectedDigestGatesParsingAndPersistence()
    {
        var dmarc = new StubDmarcIngestor(_ => DmarcIngestOutcome.Inserted);
        var tls = new StubTlsIngestor(_ => TlsReportIngestOutcome.Inserted);
        var ingestor = CreateIngestor(dmarc, tls);
        var xml = Fixture("sample-yahoo-aggregate.xml");

        await using var payload = new MemoryStream(xml, writable: false);
        var result = await ingestor.IngestAsync(
            Source,
            payload,
            new("report.xml", ExpectedContentSha256: new string('0', 64)),
            CancellationToken.None);

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(xml)),
            result.ContentSha256);
        Assert.Equal(ReportPayloadRejectionCode.ContentSha256Mismatch, Assert.Single(result.Rejections).Code);
        Assert.Equal(0, result.ReportsProcessed);
        Assert.Empty(dmarc.Reports);
        Assert.Empty(tls.Reports);

        await using var matchingPayload = new MemoryStream(xml, writable: false);
        var matching = await ingestor.IngestAsync(
            Source,
            matchingPayload,
            new("report.xml", ExpectedContentSha256: result.ContentSha256),
            CancellationToken.None);

        Assert.Equal(1, matching.DmarcInserted);
        Assert.Single(dmarc.Reports);
    }

    [Fact]
    public async Task ExternalEntityReportIsRejectedBeforePersistence()
    {
        var dmarc = new StubDmarcIngestor(_ => DmarcIngestOutcome.Inserted);
        var ingestor = CreateIngestor(
            dmarc,
            new StubTlsIngestor(_ => TlsReportIngestOutcome.Inserted));
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE feedback [<!ENTITY xxe SYSTEM "file:///not-readable">]>
            <feedback><report_metadata><org_name>&xxe;</org_name></report_metadata></feedback>
            """u8.ToArray();

        await using var payload = new MemoryStream(xml, writable: false);
        var result = await ingestor.IngestAsync(
            Source, payload, new("report.xml"), CancellationToken.None);

        Assert.Equal(1, result.DmarcRejected);
        Assert.Equal(ReportPayloadRejectionCode.InvalidDmarcReport, Assert.Single(result.Rejections).Code);
        Assert.Empty(dmarc.Reports);
    }

    private static ReportPayloadIngestor CreateIngestor(
        IDmarcReportIngestor dmarc,
        ITlsReportIngestor tls,
        ReportPayloadExtractionOptions? limits = null)
        => new(
            new BoundedReportPayloadExtractor(Options.Create(limits ?? new ReportPayloadExtractionOptions())),
            new DmarcRuaReportParser(),
            dmarc,
            new TlsRptReportParser(),
            tls);

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

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return output.ToArray();
    }

    private sealed class StubDmarcIngestor(Func<DmarcReportParseResult, DmarcIngestOutcome> outcome)
        : IDmarcReportIngestor
    {
        public List<DmarcReportParseResult> Reports { get; } = [];

        public Task<DmarcIngestOutcome> IngestParsedAsync(
            ReportSourceContext source,
            DmarcReportParseResult report,
            CancellationToken ct)
        {
            Reports.Add(report);
            return Task.FromResult(outcome(report));
        }
    }

    private sealed class StubTlsIngestor(Func<TlsRptParseResult, TlsReportIngestOutcome> outcome)
        : ITlsReportIngestor
    {
        public List<TlsRptParseResult> Reports { get; } = [];

        public Task<TlsReportIngestOutcome> IngestAsync(
            ReportSourceContext source,
            TlsRptParseResult parsed,
            CancellationToken ct)
        {
            Reports.Add(parsed);
            return Task.FromResult(outcome(parsed));
        }
    }
}
