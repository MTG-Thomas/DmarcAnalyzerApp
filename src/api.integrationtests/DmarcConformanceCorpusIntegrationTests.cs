using System.Text.Json;
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
public sealed class DmarcConformanceCorpusIntegrationTests(PostgreSqlDatabaseFixture database)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static string CorpusRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Conformance");

    [Fact]
    public async Task RawCorpusProducesExactPostgreSqlGraphAndRouting()
    {
        var source = await ResetMigrateAndSeedAsync();
        var corpusRoot = CorpusRoot;
        var manifest = ReadJson<CorpusManifest>(Path.Combine(corpusRoot, "manifest.json"));
        var outcomeFailures = new List<string>();
        var stateFailures = new List<string>();
        var expectedDomainOwners = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-a.example"] = "client-a",
            ["client-b.example"] = "client-b",
        };

        foreach (var corpusCase in manifest.Cases.OrderBy(x => x.DeliveryOrder))
        {
            var expected = ReadJson<ExpectedState>(Path.Combine(corpusRoot, corpusCase.Expected));
            Assert.Equal(corpusCase.Id, expected.CaseId);
            Assert.Equal(corpusCase.ExpectedOutcome, expected.Outcome);

            var before = await ReadGraphCountsAsync();
            var results = new List<ReportPayloadIngestResult>();

            foreach (var payload in corpusCase.Payloads)
            {
                await using var db = database.CreateDbContext();
                var ingestor = CreateIngestor(db);
                await using var stream = File.OpenRead(Path.Combine(corpusRoot, payload.Path));
                var result = await ingestor.IngestAsync(
                    source,
                    stream,
                    new(payload.FileName, payload.MediaType, payload.Sha256),
                    CancellationToken.None);
                Assert.Equal(payload.Sha256, result.ContentSha256);
                Assert.Equal(stream.Length, result.PayloadBytes);
                Assert.Equal(MapContainer(payload.Container), result.Container);
                results.Add(result);
            }

            var outcomeFailure = DescribeOutcomeFailure(corpusCase, expected, results);
            if (outcomeFailure is not null)
            {
                outcomeFailures.Add(outcomeFailure);
                continue;
            }

            foreach (var report in expected.Reports)
            {
                expectedDomainOwners[report.Key.PolicyDomain] = report.Routing?.ClientSlug ?? "client-default";
            }

            var after = await ReadGraphCountsAsync();
            var expectedDkimRows = expected.Reports.Sum(report =>
                report.Records.Sum(record => record.DkimAuth.Count));
            var expectedSpfRows = expected.Reports.Sum(report =>
                report.Records.Sum(record => record.SpfAuth.Count));

            if (after != new GraphCounts(
                    before.Reports + expected.Deltas.Reports,
                    before.Records + expected.Deltas.Records,
                    before.DkimRows + expectedDkimRows,
                    before.SpfRows + expectedSpfRows,
                    before.Ledgers + expected.Deltas.Reports))
            {
                stateFailures.Add($"{corpusCase.Id}: expected graph delta "
                    + $"reports={expected.Deltas.Reports}, records={expected.Deltas.Records}, "
                    + $"dkim={expectedDkimRows}, spf={expectedSpfRows}, ledgers={expected.Deltas.Reports}; "
                    + $"before={before}, after={after}");
                continue;
            }

            foreach (var report in expected.Reports)
            {
                try
                {
                    await AssertPersistedReportAsync(report, source);
                }
                catch (Exception ex) when (ex is Xunit.Sdk.XunitException or InvalidOperationException)
                {
                    stateFailures.Add($"{corpusCase.Id}/{report.Key.ReportId}: {ex.Message}");
                }
            }

            try
            {
                await AssertDomainClosureAsync(expectedDomainOwners);
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                stateFailures.Add($"{corpusCase.Id}/domains: {ex.Message}");
            }
        }

        Assert.True(
            outcomeFailures.Count + stateFailures.Count == 0,
            "Corpus mismatches:" + Environment.NewLine
            + string.Join(Environment.NewLine, outcomeFailures.Concat(stateFailures)));
    }

    [Fact]
    public async Task SameBusinessKeyAcrossSourcesPreservesOriginalProvenance()
    {
        var originalSource = await ResetMigrateAndSeedAsync();
        var otherClient = new Client
        {
            Name = "Synthetic other source client",
            Slug = "source-two-client",
            Timezone = "UTC",
        };
        var otherSource = new ReportSource
        {
            Name = "Synthetic other source",
            Protocol = "api",
            DefaultClientId = otherClient.Id,
        };
        otherSource.NormalizeProtocolState();

        await using (var seedDb = database.CreateDbContext())
        {
            seedDb.AddRange(otherClient, otherSource);
            await seedDb.SaveChangesAsync();
        }

        var corpusCase = ReadCorpusCase("valid-v1-plain");
        var payload = Assert.Single(corpusCase.Payloads);
        var inserted = await IngestPayloadAsync(originalSource, payload);
        var duplicate = await IngestPayloadAsync(
            new(otherSource.Id, otherClient.Id),
            payload);

        Assert.Equal(1, inserted.DmarcInserted);
        Assert.Equal(1, duplicate.DmarcDuplicates);
        Assert.Equal(new GraphCounts(1, 2, 2, 2, 1), await ReadGraphCountsAsync());

        await using var verification = database.CreateDbContext();
        Assert.Equal(originalSource.SourceId, await verification.DmarcReports.Select(x => x.ReportSourceId).SingleAsync());
        Assert.Equal(originalSource.SourceId, await verification.DmarcReportIngests.Select(x => x.ReportSourceId).SingleAsync());
        Assert.Equal(originalSource.DefaultClientId, await verification.DmarcReportIngests.Select(x => x.ClientId).SingleAsync());
        Assert.Equal(
            originalSource.DefaultClientId,
            await verification.Domains.Where(x => x.Name == "alpha.example").Select(x => x.ClientId).SingleAsync());
        Assert.Equal(
            otherClient.Id,
            await verification.ReportSources.Where(x => x.Id == otherSource.Id).Select(x => x.DefaultClientId).SingleAsync());
    }

    [Fact]
    public async Task ConcurrentRawReplayCreatesOneCompletePostgreSqlGraph()
    {
        var source = await ResetMigrateAndSeedAsync();
        var corpusCase = ReadCorpusCase("valid-v1-plain");
        var payload = Assert.Single(corpusCase.Payloads);
        var bytes = File.ReadAllBytes(Path.Combine(CorpusRoot, payload.Path));
        using var barrier = new Barrier(3);

        var first = StartAtBarrierAsync(source, payload, bytes, barrier);
        var second = StartAtBarrierAsync(source, payload, bytes, barrier);
        barrier.SignalAndWait();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, x => x.DmarcInserted == 1);
        Assert.Single(outcomes, x => x.DmarcDuplicates == 1);
        Assert.Equal(new GraphCounts(1, 2, 2, 2, 1), await ReadGraphCountsAsync());
    }

    private async Task<ReportSourceContext> ResetMigrateAndSeedAsync()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var clients = new[]
        {
            new Client { Name = "Synthetic default", Slug = "client-default", Timezone = "UTC" },
            new Client { Name = "Synthetic client A", Slug = "client-a", Timezone = "UTC" },
            new Client { Name = "Synthetic client B", Slug = "client-b", Timezone = "UTC" },
        };
        var clientsBySlug = clients.ToDictionary(x => x.Slug);
        var source = new ReportSource
        {
            Name = "Synthetic conformance source",
            Protocol = "api",
            DefaultClientId = clientsBySlug["client-default"].Id,
        };
        source.NormalizeProtocolState();

        var domains = new[]
        {
            new Domain { Name = "client-a.example", ClientId = clientsBySlug["client-a"].Id },
            new Domain { Name = "client-b.example", ClientId = clientsBySlug["client-b"].Id },
        };

        await using var db = database.CreateDbContext();
        db.AddRange(clients);
        db.Add(source);
        db.AddRange(domains);
        await db.SaveChangesAsync();
        return new(source.Id, source.DefaultClientId);
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

    private async Task<ReportPayloadIngestResult> IngestPayloadAsync(
        ReportSourceContext source,
        CorpusPayload payload)
    {
        await using var db = database.CreateDbContext();
        var ingestor = CreateIngestor(db);
        await using var stream = File.OpenRead(Path.Combine(CorpusRoot, payload.Path));
        return await ingestor.IngestAsync(
            source,
            stream,
            new(payload.FileName, payload.MediaType, payload.Sha256),
            CancellationToken.None);
    }

    private Task<ReportPayloadIngestResult> StartAtBarrierAsync(
        ReportSourceContext source,
        CorpusPayload payload,
        byte[] bytes,
        Barrier barrier)
        => Task.Run(async () =>
        {
            await using var db = database.CreateDbContext();
            var ingestor = CreateIngestor(db);
            await using var stream = new MemoryStream(bytes, writable: false);
            barrier.SignalAndWait();
            return await ingestor.IngestAsync(
                source,
                stream,
                new(payload.FileName, payload.MediaType, payload.Sha256),
                CancellationToken.None);
        });

    private static string? DescribeOutcomeFailure(
        CorpusCase corpusCase,
        ExpectedState expected,
        IReadOnlyCollection<ReportPayloadIngestResult> results)
    {
        Assert.All(results, result =>
        {
            Assert.Equal(0, result.TlsInserted);
            Assert.Equal(0, result.TlsDuplicates);
            Assert.Equal(0, result.TlsRejected);
        });

        var inserted = results.Sum(x => x.DmarcInserted);
        var duplicates = results.Sum(x => x.DmarcDuplicates);
        var rejected = results.Sum(x => x.DmarcRejected);
        var rejections = results.Sum(x => x.Rejections.Count);
        var rejectionCodes = results.SelectMany(x => x.Rejections).Select(x => x.Code).ToArray();

        switch (expected.Outcome)
        {
            case "inserted":
                return expected.Deltas.Reports == inserted && duplicates == 0
                    ? null
                    : $"{corpusCase.Id}: expected {expected.Deltas.Reports} inserts, got {inserted} "
                      + $"and {duplicates} duplicates; {DescribeResults(results)}";
            case "duplicate":
                return inserted == 0 && duplicates > 0
                    ? null
                    : $"{corpusCase.Id}: expected duplicate, got {inserted} inserts and {duplicates} duplicates; "
                      + DescribeResults(results);
            case "rejected":
                if (inserted != 0 || duplicates != 0 || rejected + rejections == 0)
                {
                    return $"{corpusCase.Id}: expected rejection, got {inserted} inserts, {duplicates} duplicates, "
                           + $"and {rejected + rejections} rejection signals; {DescribeResults(results)}";
                }

                return MatchesReasonClass(expected.ReasonClass, rejectionCodes)
                    ? null
                    : $"{corpusCase.Id}: expected rejection class {expected.ReasonClass}, "
                      + $"got [{string.Join(',', rejectionCodes)}]";
            default:
                throw new InvalidOperationException($"Unsupported corpus outcome '{expected.Outcome}'.");
        }
    }

    private static bool MatchesReasonClass(
        string? reasonClass,
        IReadOnlyCollection<ReportPayloadRejectionCode> codes)
        => reasonClass switch
        {
            "xml_malformed" or "schema_invalid" => codes.Contains(ReportPayloadRejectionCode.InvalidDmarcReport),
            "archive_invalid" => codes.Contains(ReportPayloadRejectionCode.CorruptContainer),
            "size_limit" => codes.Any(code => code is
                ReportPayloadRejectionCode.RequestTooLarge
                or ReportPayloadRejectionCode.ArchiveEntryLimitExceeded
                or ReportPayloadRejectionCode.EntryTooLarge
                or ReportPayloadRejectionCode.ExpandedSizeLimitExceeded
                or ReportPayloadRejectionCode.CompressionRatioExceeded),
            _ => false,
        };

    private static string DescribeResults(IEnumerable<ReportPayloadIngestResult> results)
        => string.Join(
            "; ",
            results.Select(result =>
                $"inserted={result.DmarcInserted}, duplicate={result.DmarcDuplicates}, "
                + $"rejected={result.DmarcRejected}, reasons=[{string.Join(',', result.Rejections.Select(x => x.Code))}]"));

    private async Task AssertPersistedReportAsync(
        ExpectedReport expected,
        ReportSourceContext source)
    {
        var begin = DateTimeOffset.FromUnixTimeSeconds(expected.Key.RangeBeginEpoch).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeSeconds(expected.Key.RangeEndEpoch).UtcDateTime;

        await using var db = database.CreateDbContext();
        var report = await db.DmarcReports
            .Include(x => x.Domain)
            .Include(x => x.Records)
                .ThenInclude(x => x.DkimAuthResults)
            .Include(x => x.Records)
                .ThenInclude(x => x.SpfAuthResults)
            .SingleAsync(x =>
                x.Domain!.Name == expected.Key.PolicyDomain
                && x.ReportId == expected.Key.ReportId
                && x.RangeBeginUtc == begin
                && x.RangeEndUtc == end);
        var domain = Assert.IsType<Domain>(report.Domain);

        Assert.Equal(source.SourceId, report.ReportSourceId);
        Assert.Equal(expected.Metadata.Organization, report.OrganizationName);
        Assert.Equal(expected.Metadata.RecordCount, report.RecordCount);
        Assert.Equal(expected.Policy.P, report.PublishedPolicy);
        Assert.Equal(expected.Policy.Sp, report.SubdomainPolicy);
        Assert.Equal(expected.Policy.Pct ?? 100, report.PublishedPct);
        Assert.Equal(MapAlignment(expected.Policy.Adkim), report.DkimAlignment);
        Assert.Equal(MapAlignment(expected.Policy.Aspf), report.SpfAlignment);

        var expectedClientSlug = expected.Routing?.ClientSlug ?? "client-default";
        Assert.Equal(
            expectedClientSlug,
            await db.Clients
                .Where(x => x.Id == domain.ClientId)
                .Select(x => x.Slug)
                .SingleAsync());

        var ledger = await db.DmarcReportIngests.SingleAsync(x =>
            x.PolicyDomain == expected.Key.PolicyDomain
            && x.ReportId == expected.Key.ReportId
            && x.ReportRangeBeginUtc == begin
            && x.ReportRangeEndUtc == end);
        Assert.Equal(source.SourceId, ledger.ReportSourceId);
        Assert.Equal(domain.ClientId, ledger.ClientId);
        Assert.Equal(report.OrganizationName, ledger.OrganizationName);
        Assert.Equal(report.RecordCount, ledger.RecordCount);

        var expectedRecords = expected.Records
            .Select(record => ProjectExpectedRecord(record, begin))
            .OrderBy(x => x.SortKey, StringComparer.Ordinal)
            .ToArray();
        var actualRecords = report.Records
            .Select(ProjectActualRecord)
            .OrderBy(x => x.SortKey, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedRecords, actualRecords);
    }

    private async Task<GraphCounts> ReadGraphCountsAsync()
    {
        await using var db = database.CreateDbContext();
        return new(
            await db.DmarcReports.CountAsync(),
            await db.DmarcReportRecords.CountAsync(),
            await db.DmarcReportRecordDkimAuthResults.CountAsync(),
            await db.DmarcReportRecordSpfAuthResults.CountAsync(),
            await db.DmarcReportIngests.CountAsync());
    }

    private async Task AssertDomainClosureAsync(IReadOnlyDictionary<string, string> expectedDomainOwners)
    {
        await using var db = database.CreateDbContext();
        var actual = await db.Domains
            .Join(db.Clients, domain => domain.ClientId, client => client.Id,
                (domain, client) => new { domain.Name, ClientSlug = client.Slug })
            .OrderBy(x => x.Name)
            .Select(x => new DomainProjection(x.Name, x.ClientSlug))
            .ToArrayAsync();
        var expected = expectedDomainOwners
            .Select(x => new DomainProjection(x.Key, x.Value))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static PersistedRecordProjection ProjectExpectedRecord(ExpectedRecord record, DateTime reportRangeBeginUtc)
        => new(
            record.SourceIp,
            record.MessageCount,
            record.Disposition,
            record.Dkim,
            record.Spf,
            record.HeaderFrom,
            record.EnvelopeFrom ?? string.Empty,
            record.EnvelopeTo ?? string.Empty,
            reportRangeBeginUtc,
            string.Join('\u001e', record.DkimAuth
                .Select(x => $"{x.Domain}\u001f{x.Selector ?? string.Empty}\u001f{x.Result}")
                .Order(StringComparer.Ordinal)),
            string.Join('\u001e', record.SpfAuth
                .Select(x => $"{x.Domain}\u001f{x.Scope ?? string.Empty}\u001f{x.Result}")
                .Order(StringComparer.Ordinal)));

    private static PersistedRecordProjection ProjectActualRecord(DmarcReportRecord record)
        => new(
            record.SourceIp,
            record.MessageCount,
            record.Disposition,
            record.DkimResult,
            record.SpfResult,
            record.HeaderFrom,
            record.EnvelopeFrom,
            record.EnvelopeTo,
            record.ReportRangeBeginUtc,
            string.Join('\u001e', record.DkimAuthResults
                .Select(x => $"{x.Domain}\u001f{x.Selector}\u001f{x.Result}")
                .Order(StringComparer.Ordinal)),
            string.Join('\u001e', record.SpfAuthResults
                .Select(x => $"{x.Domain}\u001f{x.Scope}\u001f{x.Result}")
                .Order(StringComparer.Ordinal)));

    private static string MapAlignment(string? alignment)
        => string.Equals(alignment, "s", StringComparison.OrdinalIgnoreCase)
            ? "strict"
            : "relaxed";

    private static ReportPayloadContainer MapContainer(string container)
        => container switch
        {
            "plain" => ReportPayloadContainer.Bare,
            "gzip" => ReportPayloadContainer.Gzip,
            "zip" => ReportPayloadContainer.Zip,
            _ => throw new InvalidOperationException($"Unsupported corpus container '{container}'."),
        };

    private static T ReadJson<T>(string path)
        where T : notnull
        => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions)
           ?? throw new InvalidOperationException($"Could not deserialize {path}.");

    private static CorpusCase ReadCorpusCase(string caseId)
        => ReadJson<CorpusManifest>(Path.Combine(CorpusRoot, "manifest.json"))
            .Cases
            .Single(x => x.Id == caseId);

    private sealed record GraphCounts(int Reports, int Records, int DkimRows, int SpfRows, int Ledgers);

    private sealed record DomainProjection(string Name, string ClientSlug);

    private sealed record PersistedRecordProjection(
        string SourceIp,
        int MessageCount,
        string Disposition,
        string Dkim,
        string Spf,
        string HeaderFrom,
        string EnvelopeFrom,
        string EnvelopeTo,
        DateTime ReportRangeBeginUtc,
        string DkimAuth,
        string SpfAuth)
    {
        public string SortKey => string.Join(
            '\u001f',
            SourceIp,
            MessageCount,
            Disposition,
            Dkim,
            Spf,
            HeaderFrom,
            EnvelopeFrom,
            EnvelopeTo,
            ReportRangeBeginUtc.ToString("O"),
            DkimAuth,
            SpfAuth);
    }

    private sealed record CorpusManifest(IReadOnlyList<CorpusCase> Cases);

    private sealed record CorpusCase(
        string Id,
        int DeliveryOrder,
        string Expected,
        string ExpectedOutcome,
        IReadOnlyList<CorpusPayload> Payloads);

    private sealed record CorpusPayload(
        string FileName,
        string MediaType,
        string Path,
        string Sha256,
        string Container);

    private sealed record ExpectedState(
        string CaseId,
        string Outcome,
        string? ReasonClass,
        ExpectedDeltas Deltas,
        IReadOnlyList<ExpectedReport> Reports);

    private sealed record ExpectedDeltas(int Reports, int Records);

    private sealed record ExpectedReport(
        ExpectedReportKey Key,
        ExpectedMetadata Metadata,
        ExpectedPolicy Policy,
        IReadOnlyList<ExpectedRecord> Records,
        ExpectedRouting? Routing);

    private sealed record ExpectedReportKey(
        string PolicyDomain,
        string ReportId,
        long RangeBeginEpoch,
        long RangeEndEpoch);

    private sealed record ExpectedMetadata(string Organization, int RecordCount);

    private sealed record ExpectedPolicy(
        string P,
        string? Sp,
        int? Pct,
        string? Adkim,
        string? Aspf);

    private sealed record ExpectedRouting(string ClientSlug);

    private sealed record ExpectedRecord(
        string SourceIp,
        int MessageCount,
        string Disposition,
        string Dkim,
        string Spf,
        string HeaderFrom,
        string? EnvelopeFrom,
        string? EnvelopeTo,
        IReadOnlyList<ExpectedAuthResult> DkimAuth,
        IReadOnlyList<ExpectedAuthResult> SpfAuth);

    private sealed record ExpectedAuthResult(
        string Domain,
        string Result,
        string? Selector,
        string? Scope);
}
