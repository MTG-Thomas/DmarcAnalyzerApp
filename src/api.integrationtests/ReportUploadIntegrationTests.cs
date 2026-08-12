using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class ReportUploadIntegrationTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task AuthenticatedUploadInsertsThenReplaysWithoutChildDuplicates()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var xml = Fixture();

        await using var db = database.CreateDbContext();
        var token = (await new ApiSourceCredentialService(db)
            .IssueAsync(seeded.SourceAId, default)).Value!.Token;
        var handler = Handler(db);

        var inserted = await handler.HandleAsync(
            Request(xml, token), seeded.SourceAId, default);
        var duplicate = await handler.HandleAsync(
            Request(xml, token), seeded.SourceAId, default);

        Assert.Equal(201, Status(inserted));
        Assert.Equal(200, Status(duplicate));
        Assert.Equal(1, Response(inserted).Inserted);
        Assert.Equal(1, Response(duplicate).Duplicates);

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReports.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecords.CountAsync());
        Assert.Equal(2, await verification.DmarcReportRecordDkimAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecordSpfAuthResults.CountAsync());
        Assert.Equal(1, await verification.DmarcReportIngests.CountAsync());
        Assert.Equal(
            seeded.SourceAId,
            await verification.DmarcReports.Select(x => x.ReportSourceId).SingleAsync());
    }

    [Fact]
    public async Task SourceATokenCannotAddressSourceBAndNewDomainsStayWithSourceAClient()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var xml = Fixture();

        await using var db = database.CreateDbContext();
        var tokenA = (await new ApiSourceCredentialService(db)
            .IssueAsync(seeded.SourceAId, default)).Value!.Token;
        var handler = Handler(db);

        var crossSource = await handler.HandleAsync(
            Request(xml, tokenA), seeded.SourceBId, default);

        Assert.Equal(401, Status(crossSource));
        await using (var untouched = database.CreateDbContext())
        {
            Assert.Empty(await untouched.DmarcReports.ToListAsync());
            Assert.Empty(await untouched.Domains.ToListAsync());
        }

        var ownSource = await handler.HandleAsync(
            Request(xml, tokenA), seeded.SourceAId, default);
        Assert.Equal(201, Status(ownSource));

        await using var verification = database.CreateDbContext();
        Assert.Equal(
            seeded.ClientAId,
            await verification.Domains.Select(x => x.ClientId).SingleAsync());
        Assert.Equal(
            seeded.SourceAId,
            await verification.DmarcReports.Select(x => x.ReportSourceId).SingleAsync());
        Assert.DoesNotContain(
            await verification.DmarcReportIngests.ToListAsync(),
            x => x.ClientId == seeded.ClientBId || x.ReportSourceId == seeded.SourceBId);
    }

    [Fact]
    public async Task ApiSourceCannotRouteDmarcIntoAnExistingOtherClientDomain()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var xml = Fixture();
        var policyDomain = new DmarcRuaReportParser()
            .Parse(new MemoryStream(xml, writable: false))
            .PolicyDomain;

        await using var db = database.CreateDbContext();
        db.Domains.Add(new Domain
        {
            ClientId = seeded.ClientBId,
            Name = policyDomain,
        });
        await db.SaveChangesAsync();
        var tokenA = (await new ApiSourceCredentialService(db)
            .IssueAsync(seeded.SourceAId, default)).Value!.Token;

        var result = await Handler(db).HandleAsync(
            Request(xml, tokenA), seeded.SourceAId, default);

        Assert.Equal(422, Status(result));
        Assert.Equal(["InvalidDmarcReport"], Response(result).RejectionCodes);
        await using var verification = database.CreateDbContext();
        var domain = await verification.Domains.SingleAsync();
        Assert.Equal(seeded.ClientBId, domain.ClientId);
        Assert.Empty(await verification.DmarcReports.ToListAsync());
        Assert.Empty(await verification.DmarcReportIngests.ToListAsync());
    }

    [Fact]
    public async Task ApiSourceTlsConflictDoesNotLeaveEarlierNewDomainBehind()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        const string existingDomain = "company-y.example";
        const string newDomain = "new-a.example";
        var json = JsonNode.Parse(Fixture("sample-rfc8460-tls.json"))!.AsObject();
        var policies = json["policies"]!.AsArray();
        var existingPolicy = policies[0]!.DeepClone();
        policies[0]!["policy"]!["policy-domain"] = newDomain;
        policies.Add(existingPolicy);
        var payload = JsonSerializer.SerializeToUtf8Bytes(json);

        await using var db = database.CreateDbContext();
        db.Domains.Add(new Domain
        {
            ClientId = seeded.ClientBId,
            Name = existingDomain,
        });
        await db.SaveChangesAsync();
        var tokenA = (await new ApiSourceCredentialService(db)
            .IssueAsync(seeded.SourceAId, default)).Value!.Token;

        var result = await Handler(db).HandleAsync(
            Request(payload, tokenA), seeded.SourceAId, default);

        Assert.Equal(422, Status(result));
        Assert.Equal(["InvalidTlsReport"], Response(result).RejectionCodes);
        await using var verification = database.CreateDbContext();
        Assert.Equal([existingDomain], await verification.Domains.Select(x => x.Name).ToListAsync());
        Assert.Empty(await verification.SmtpTlsReports.ToListAsync());
        Assert.Empty(await verification.SmtpTlsReportPolicies.ToListAsync());
        Assert.Empty(await verification.TlsReportIngests.ToListAsync());
        Assert.DoesNotContain(await verification.Domains.ToListAsync(), x => x.Name == newDomain);
    }

    [Fact]
    public async Task DigestMismatchAndLyingContentLengthLimitWriteNothing()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var xml = Fixture();

        await using var db = database.CreateDbContext();
        var token = (await new ApiSourceCredentialService(db)
            .IssueAsync(seeded.SourceAId, default)).Value!.Token;

        var wrongDigest = new string('0', 64);
        var mismatch = await Handler(db).HandleAsync(
            Request(xml, token, wrongDigest), seeded.SourceAId, default);
        Assert.Equal(422, Status(mismatch));
        Assert.Equal(["ContentSha256Mismatch"], Response(mismatch).RejectionCodes);

        var lyingLength = Request(xml, token);
        lyingLength.Request.ContentLength = 1;
        var limited = await Handler(db, maxRequestBytes: 128).HandleAsync(
            lyingLength, seeded.SourceAId, default);
        Assert.Equal(413, Status(limited));
        Assert.Contains("RequestTooLarge", Response(limited).RejectionCodes);

        await using var verification = database.CreateDbContext();
        Assert.Empty(await verification.Domains.ToListAsync());
        Assert.Empty(await verification.DmarcReports.ToListAsync());
        Assert.Empty(await verification.DmarcReportRecords.ToListAsync());
        Assert.Empty(await verification.DmarcReportIngests.ToListAsync());
    }

    [Fact]
    public async Task PersistenceFailureRollsBackAndCorrectedReplayCanInsert()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var valid = Encoding.UTF8.GetString(Fixture());
        var invalid = Encoding.UTF8.GetBytes(valid.Replace(
            "<selector>k1</selector>",
            $"<selector>{new string('x', 256)}</selector>",
            StringComparison.Ordinal));

        await using var db = database.CreateDbContext();
        var token = (await new ApiSourceCredentialService(db)
            .IssueAsync(seeded.SourceAId, default)).Value!.Token;
        var handler = Handler(db);

        await Assert.ThrowsAnyAsync<Exception>(() => handler.HandleAsync(
            Request(invalid, token), seeded.SourceAId, default));

        await using (var failed = database.CreateDbContext())
        {
            Assert.Empty(await failed.DmarcReports.ToListAsync());
            Assert.Empty(await failed.DmarcReportRecords.ToListAsync());
            Assert.Empty(await failed.DmarcReportIngests.ToListAsync());
        }

        var corrected = await handler.HandleAsync(
            Request(Encoding.UTF8.GetBytes(valid), token), seeded.SourceAId, default);
        Assert.Equal(201, Status(corrected));

        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.DmarcReports.CountAsync());
        Assert.Equal(1, await verification.DmarcReportRecords.CountAsync());
        Assert.Equal(1, await verification.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task CancellationDuringChildWriteRollsBackTheCompleteGraph()
    {
        var seeded = await ResetMigrateAndSeedAsync();
        var xml = Fixture();

        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION delay_report_record_insert() RETURNS trigger AS $$
            BEGIN
                PERFORM pg_sleep(10);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER delay_report_record_insert
                BEFORE INSERT ON dmarc_report_record
                FOR EACH ROW EXECUTE FUNCTION delay_report_record_insert();
            """);
        var token = (await new ApiSourceCredentialService(db)
            .IssueAsync(seeded.SourceAId, default)).Value!.Token;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(db).HandleAsync(
            Request(xml, token),
            seeded.SourceAId,
            cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);

        await using var verification = database.CreateDbContext();
        Assert.Empty(await verification.DmarcReports.ToListAsync());
        Assert.Empty(await verification.DmarcReportRecords.ToListAsync());
        Assert.Empty(await verification.DmarcReportRecordDkimAuthResults.ToListAsync());
        Assert.Empty(await verification.DmarcReportRecordSpfAuthResults.ToListAsync());
        Assert.Empty(await verification.DmarcReportIngests.ToListAsync());
    }

    private static ReportUploadHandler Handler(
        DmarcAnalyzerDbContext db,
        int maxRequestBytes = 25 * 1024 * 1024)
    {
        var resolver = new DomainIngestResolver(db);
        var limits = new ReportPayloadExtractionOptions
        {
            MaxRequestBytes = maxRequestBytes,
            MaxEntryBytes = maxRequestBytes,
            MaxExpandedBytes = maxRequestBytes,
        };
        var ingestor = new ReportPayloadIngestor(
            new BoundedReportPayloadExtractor(Options.Create(limits)),
            new DmarcRuaReportParser(),
            new DmarcReportIngestor(db, resolver),
            new TlsRptReportParser(),
            new TlsReportIngestor(db, resolver));

        return new(
            new ApiSourceAuthenticator(db),
            ingestor,
            Options.Create(limits),
            new NullAuditLog());
    }

    private async Task<SeededSources> ResetMigrateAndSeedAsync()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var clientA = new Client
        {
            Name = "Upload client A",
            Slug = $"upload-a-{Guid.NewGuid():N}",
            Timezone = "UTC",
        };
        var clientB = new Client
        {
            Name = "Upload client B",
            Slug = $"upload-b-{Guid.NewGuid():N}",
            Timezone = "UTC",
        };
        var sourceA = new ReportSource
        {
            Name = "Bifrost A",
            Protocol = "api",
            UseTls = null,
            DefaultClientId = clientA.Id,
        };
        var sourceB = new ReportSource
        {
            Name = "Bifrost B",
            Protocol = "api",
            UseTls = null,
            DefaultClientId = clientB.Id,
        };

        await using var db = database.CreateDbContext();
        db.AddRange(clientA, clientB, sourceA, sourceB);
        await db.SaveChangesAsync();
        return new(clientA.Id, clientB.Id, sourceA.Id, sourceB.Id);
    }

    private static DefaultHttpContext Request(byte[] bytes, string token, string? digest = null)
    {
        digest ??= Convert.ToHexStringLower(SHA256.HashData(bytes));
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes, writable: false);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/octet-stream";
        context.Request.Headers.Authorization = $"Bearer {token}";
        context.Request.Headers["X-Content-SHA256"] = digest;
        context.Request.Headers["Idempotency-Key"] = $"sha256:{digest}";
        return context;
    }

    private static byte[] Fixture()
        => Fixture("sample-yahoo-aggregate.xml");

    private static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            name));

    private static int Status(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static ReportUploadResponse Response(IResult result)
        => Assert.IsType<ReportUploadResponse>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private sealed record SeededSources(
        Guid ClientAId,
        Guid ClientBId,
        Guid SourceAId,
        Guid SourceBId);

    private sealed class NullAuditLog : IAuditLog
    {
        public Task RecordAsync(string eventType, string summary, string? targetType = null, Guid? targetId = null, Guid? clientId = null, string? details = null, string? actorEmailOverride = null, Guid? actorUserIdOverride = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RecordSystemAsync(string eventType, string summary, string? details = null, Guid? clientId = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
