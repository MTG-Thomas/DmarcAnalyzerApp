using DmarcAnalyzer.Api.Application.MailboxSources;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.MailboxSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ApiMailboxSourceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static MailboxSourceService Service(DmarcAnalyzerDbContext db)
        => new(db, new AesGcmCredentialProtector(Convert.ToBase64String(new byte[32])),
            TestCurrentUserContext.Admin());

    [Fact]
    public async Task CreatesApiSourceWithoutMailboxConfiguration()
    {
        await using var db = NewDb();
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var result = await Service(db).CreateAsync(new CreateMailboxSourceRequest
        {
            Name = "Bifrost upload",
            Protocol = "api",
            DefaultClientId = client.Id,
        }, default);

        Assert.True(result.IsSuccess);
        var source = await db.MailboxSources.SingleAsync();
        Assert.Equal("api", source.Protocol);
        Assert.Null(source.Host);
        Assert.Null(source.Port);
        Assert.Null(source.UseTls);
        Assert.Null(source.Username);
        Assert.Null(source.PasswordEncrypted);
        Assert.False(source.DeleteAfterRetention);
    }

    [Fact]
    public async Task TransitionToApiClearsMailboxOnlyState()
    {
        await using var db = NewDb();
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        var source = new MailboxSource
        {
            Name = "Mailbox",
            Protocol = "imap",
            Host = "imap.example",
            Port = 993,
            UseTls = true,
            Username = "reports@example",
            PasswordEncrypted = "encrypted",
            DefaultClientId = client.Id,
            DeleteAfterRetention = true,
            OldestMessageAtUtc = DateTime.UtcNow.AddDays(-3),
            LastSuccessSyncAtUtc = DateTime.UtcNow,
            LastProcessedUid = 42,
            LastProcessedUidValidity = 7,
        };
        db.AddRange(client, source);
        await db.SaveChangesAsync();

        var result = await Service(db).UpdateAsync(source.Id, new UpdateMailboxSourceRequest
        {
            Protocol = "api",
        }, default);

        Assert.True(result.IsSuccess);
        Assert.Equal("api", source.Protocol);
        Assert.Null(source.Host);
        Assert.Null(source.Port);
        Assert.Null(source.UseTls);
        Assert.Null(source.Username);
        Assert.Null(source.PasswordEncrypted);
        Assert.Null(source.OldestMessageAtUtc);
        Assert.Null(source.LastSuccessSyncAtUtc);
        Assert.Null(source.LastProcessedUid);
        Assert.Null(source.LastProcessedUidValidity);
        Assert.False(source.DeleteAfterRetention);
    }

    [Fact]
    public async Task TransitionFromApiRequiresCompleteMailboxConfiguration()
    {
        await using var db = NewDb();
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        var source = new MailboxSource
        {
            Name = "Upload",
            Protocol = "api",
            Host = null,
            Port = null,
            UseTls = null,
            Username = null,
            PasswordEncrypted = null,
            DefaultClientId = client.Id,
        };
        db.AddRange(client, source);
        await db.SaveChangesAsync();

        var refused = await Service(db).UpdateAsync(source.Id, new UpdateMailboxSourceRequest
        {
            Protocol = "imap",
            Host = "imap.example",
        }, default);

        Assert.False(refused.IsSuccess);
        Assert.Equal(400, refused.StatusCode);
        Assert.Equal("api", source.Protocol);

        var changed = await Service(db).UpdateAsync(source.Id, new UpdateMailboxSourceRequest
        {
            Protocol = "imap",
            Host = "imap.example",
            Port = 993,
            UseTls = true,
            Username = "reports@example",
            Password = "secret",
        }, default);

        Assert.True(changed.IsSuccess);
        Assert.Equal("imap", source.Protocol);
        Assert.StartsWith(AesGcmCredentialProtector.Prefix, source.PasswordEncrypted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiSourcesDoNotChangeMailboxHealthTotals()
    {
        await using var db = NewDb();
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        var mailbox = new MailboxSource
        {
            Name = "Mailbox", Host = "imap.example", Port = 993, UseTls = true,
            Username = "reports@example", PasswordEncrypted = "encrypted",
            DefaultClientId = client.Id,
        };
        var api = new MailboxSource
        {
            Name = "API", Protocol = "api", Host = null, Port = null, UseTls = null,
            Username = null, PasswordEncrypted = null, DefaultClientId = client.Id,
        };
        db.AddRange(client, mailbox, api);
        db.MailboxSyncRuns.AddRange(
            new MailboxSyncRun
            {
                MailboxSourceId = mailbox.Id, Trigger = "scheduled", Status = "success",
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-2), FinishedAtUtc = DateTime.UtcNow,
            },
            new MailboxSyncRun
            {
                MailboxSourceId = api.Id, Trigger = "manual", Status = "failed",
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-1), FinishedAtUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var summary = await TestAnalytics.Service(db, TestCurrentUserContext.Admin())
            .GetSummaryAsync(30, default);

        Assert.Equal(1, summary.Mailboxes!.Total);
        Assert.Equal(1, summary.Mailboxes.Healthy);
        Assert.Equal(0, summary.Mailboxes.Failing);
    }
}
