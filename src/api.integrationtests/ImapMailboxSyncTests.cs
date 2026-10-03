using System.IO.Compression;
using System.Text;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// A real IMAP server, a real message, a real database: the pass an operator gets.
/// <para>
/// The IMAP transport is the oldest path in the application and had no test driving it
/// against a server — only the POP3 and S3 transports added later arrived with one. The
/// checkpoint bugs this application has actually shipped were all IMAP-shaped: a UID that
/// survived a mailbox recreation and re-read 5,162 messages, a caught-up range that the
/// server normalised back onto the newest message. Those live between the pieces, so this
/// asserts them end to end: a report lands, the checkpoint is a UID plus the generation
/// that gives it meaning, and a caught-up mailbox scans nothing.
/// </para>
/// </summary>
[Collection(PostgreSqlCollections.Persistence)]
public sealed class ImapMailboxSyncTests(PostgreSqlDatabaseFixture database) : IAsyncLifetime
{
    private const int ImapPort = 3143;
    private const int SmtpPort = 3025;
    private const string Mailbox = "rua@acme.test";

    // GreenMail's own quirk, not IMAP's: -Dgreenmail.users=rua:secret@acme.test creates the
    // address rua@acme.test but the login is the local part alone, and authenticating with
    // the address gets "User 'rua@acme.test' not found".
    private const string MailboxLogin = "rua";
    private const string MailboxPassword = "secret";

    private static readonly Guid ClientId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid SourceId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    // GreenMail rather than a hand-rolled fake: the point of this suite is the behaviour a
    // stub would have to be written to have. Plaintext, because TLS is MailKit's concern —
    // what is under test is the checkpointing.
    private readonly IContainer _mail = new ContainerBuilder("greenmail/standalone:2.1.9")
        .WithEnvironment(
            "GREENMAIL_OPTS",
            "-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap " +
            $"-Dgreenmail.users={MailboxLogin}:{MailboxPassword}@{Mailbox.Split('@')[1]} " +
            "-Dgreenmail.hostname=0.0.0.0")
        .WithPortBinding(ImapPort, true)
        .WithPortBinding(SmtpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilMessageIsLogged("Starting GreenMail API server"))
        .Build();

    public async Task InitializeAsync()
    {
        await _mail.StartAsync();
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using var db = database.CreateDbContext();
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 12, Timezone = "UTC",
        });
        db.ReportSources.Add(new ReportSource
        {
            Id = SourceId,
            Name = "Acme RUA over IMAP",
            Protocol = ReportSourceProtocols.Imap,
            Host = _mail.Hostname,
            Port = _mail.GetMappedPublicPort(ImapPort),
            UseTls = false,
            Username = MailboxLogin,
            PasswordEncrypted = MailboxPassword,
            DefaultClientId = ClientId,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _mail.DisposeAsync();

    [Fact]
    public async Task AReportInAnImapMailboxIsIngestedAndCheckpointed()
    {
        await DeliverReportAsync("report-1", "acme.test");

        var result = await SyncAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.MessagesScanned);
        Assert.Equal(1, result.ReportsInserted);
        Assert.Equal(0, result.ParseFailures);

        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());

        var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);

        // A UID inside a generation, and nothing in the POP3 column. The UID alone names
        // no message once the mailbox is recreated, which is why the validity travels
        // with it; a placeholder in the UIDL column would claim a checkpoint the
        // protocol cannot honour.
        Assert.True(source.LastProcessedUid > 0);
        Assert.NotNull(source.LastProcessedUidValidity);
        Assert.Null(source.LastProcessedUidl);

        Assert.Equal("success", await LatestRunStatusAsync());
    }

    /// <summary>
    /// The 5,162-pass bug, asserted rather than reasoned about: once the mailbox is
    /// caught up, a pass scans nothing — it does not fetch the newest message again and
    /// find it is a duplicate.
    /// </summary>
    [Fact]
    public async Task ASecondPassOverACaughtUpMailboxScansNothing()
    {
        await DeliverReportAsync("report-1", "acme.test");
        await SyncAsync();

        var second = await SyncAsync();

        Assert.True(second.Success, second.Error);
        Assert.Equal(0, second.MessagesScanned);
        Assert.Equal(0, second.ReportsInserted);
        Assert.Equal(0, second.ReportsSkippedAsDuplicate);
    }

    /// <summary>
    /// New mail after a checkpoint is picked up from the checkpoint, not from the start —
    /// the case that distinguishes a working checkpoint from one that merely happens to be
    /// stored.
    /// </summary>
    [Fact]
    public async Task MailArrivingAfterACheckpointIsPickedUpFromThere()
    {
        await DeliverReportAsync("report-1", "acme.test");
        await SyncAsync();

        await DeliverReportAsync("report-2", "acme.test");
        var second = await SyncAsync();

        Assert.Equal(1, second.MessagesScanned);
        Assert.Equal(1, second.ReportsInserted);

        await using var db = database.CreateDbContext();
        Assert.Equal(2, await db.DmarcReports.CountAsync());
    }

    /// <summary>
    /// A message with nothing to extract still has to advance the checkpoint. If it did not,
    /// one piece of unrelated mail in the report mailbox would stall the pass on it for ever.
    /// </summary>
    [Fact]
    public async Task AMessageWithNoAttachmentIsStillCheckpointed()
    {
        await DeliverAsync(NewMessage("Just a note", body: "no attachment here"));

        var result = await SyncAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.MessagesScanned);
        Assert.Equal(0, result.ReportsInserted);

        await using var db = database.CreateDbContext();
        Assert.True((await db.ReportSources.SingleAsync(x => x.Id == SourceId)).LastProcessedUid > 0);

        Assert.Equal(0, (await SyncAsync()).MessagesScanned);
    }

    /// <summary>
    /// A source pointed at a host that is not there fails as a failure — with the reason on
    /// the run row, which is the only place an operator will see it.
    /// </summary>
    [Fact]
    public async Task AnUnreachableMailboxFailsTheRunWithAReason()
    {
        await using (var db = database.CreateDbContext())
        {
            var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);
            source.Port = 1;
            await db.SaveChangesAsync();
        }

        var result = await SyncAsync();

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal("failed", await LatestRunStatusAsync());
    }

    private async Task<MailboxSyncResult> SyncAsync()
    {
        await using var db = database.CreateDbContext();

        var service = new MailboxSyncService(
            db,
            new ReportPayloadIngestor(
                new BoundedReportPayloadExtractor(Options.Create(new ReportPayloadExtractionOptions())),
                new DmarcRuaReportParser(),
                new DmarcReportIngestor(db, new DomainIngestResolver(db)),
                new TlsRptReportParser(),
                new TlsReportIngestor(db, new DomainIngestResolver(db))),
            new NullCredentialProtector(),
            new ArchiveOff(),
            new PolledSourceTransportFactory(
            [
                new ImapMailboxTransport(NullLogger<ImapMailboxTransport>.Instance),
                new Pop3MailboxTransport(NullLogger<Pop3MailboxTransport>.Instance),
            ]),
            Options.Create(new WorkerOptions()),
            NullLogger<MailboxSyncService>.Instance);

        var result = await service.SyncReportSourceAsync(SourceId, "test", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    private async Task<string?> LatestRunStatusAsync()
    {
        await using var db = database.CreateDbContext();
        return await db.MailboxSyncRuns
            .Where(x => x.ReportSourceId == SourceId)
            .OrderByDescending(x => x.StartedAtUtc)
            .Select(x => x.Status)
            .FirstOrDefaultAsync();
    }

    private async Task DeliverReportAsync(string reportId, string policyDomain)
    {
        var message = NewMessage(
            $"Report domain: {policyDomain} Submitter: google.com Report-ID: {reportId}",
            body: "Report attached.");

        var body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Report attached." },
            new MimePart("application", "gzip")
            {
                Content = new MimeContent(new MemoryStream(GzipReport(reportId, policyDomain))),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = $"google.com!{policyDomain}!1754006400!1754092800.xml.gz",
            },
        };
        message.Body = body;

        await DeliverAsync(message);
    }

    private static MimeMessage NewMessage(string subject, string body)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Date = DateTimeOffset.UtcNow,
        };
        message.From.Add(new MailboxAddress("noreply", "noreply@google.com"));
        message.To.Add(new MailboxAddress("RUA", Mailbox));
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private async Task DeliverAsync(MimeMessage message)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            _mail.Hostname, _mail.GetMappedPublicPort(SmtpPort), SecureSocketOptions.None);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }

    private static byte[] GzipReport(string reportId, string policyDomain)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8" ?>
            <feedback>
              <report_metadata>
                <org_name>google.com</org_name>
                <email>noreply-dmarc-support@google.com</email>
                <report_id>{reportId}</report_id>
                <date_range><begin>1754006400</begin><end>1754092800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>{policyDomain}</domain>
                <adkim>r</adkim><aspf>r</aspf><p>none</p><sp>none</sp><pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>203.0.113.4</source_ip>
                  <count>5</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>{policyDomain}</header_from></identifiers>
                <auth_results>
                  <dkim><domain>{policyDomain}</domain><result>pass</result></dkim>
                  <spf><domain>{policyDomain}</domain><result>pass</result></spf>
                </auth_results>
              </record>
            </feedback>
            """;

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(xml));
        }

        return compressed.ToArray();
    }

    /// <summary>
    /// Archiving off, which is the default and the only state that keeps this suite from
    /// needing an object store too.
    /// </summary>
    private sealed class ArchiveOff : IReportMailArchive
    {
        public bool IsEnabled => false;

        public Task<bool> TryArchiveAsync(
            MimeMessage message, Guid reportSourceId, ReportMailIdentity identity,
            DateTime receivedAtUtc, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> ExistsAsync(
            Guid reportSourceId, ReportMailIdentity identity,
            DateTime receivedAtUtc, CancellationToken ct) => Task.FromResult(false);
    }
}
