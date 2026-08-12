using DmarcAnalyzer.Api.Application.ReportSources;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ApiReportSourceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static ReportSourceService Service(DmarcAnalyzerDbContext db)
        => new(db, new AesGcmCredentialProtector(Convert.ToBase64String(new byte[32])));

    [Fact]
    public async Task CreatesApiSourceWithoutMailboxConfiguration()
    {
        await using var db = NewDb();
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var result = await Service(db).CreateAsync(new CreateReportSourceRequest
        {
            Name = "Bifrost upload",
            Protocol = "api",
            DefaultClientId = client.Id,
        }, default);

        Assert.True(result.IsSuccess);
        var source = await db.ReportSources.SingleAsync();
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
        var source = new ReportSource
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

        var result = await Service(db).UpdateAsync(source.Id, new UpdateReportSourceRequest
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
        var source = new ReportSource
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

        var refused = await Service(db).UpdateAsync(source.Id, new UpdateReportSourceRequest
        {
            Protocol = "imap",
            Host = "imap.example",
        }, default);

        Assert.False(refused.IsSuccess);
        Assert.Equal(400, refused.StatusCode);
        Assert.Equal("api", source.Protocol);

        var changed = await Service(db).UpdateAsync(source.Id, new UpdateReportSourceRequest
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
        var mailbox = new ReportSource
        {
            Name = "Mailbox", Host = "imap.example", Port = 993, UseTls = true,
            Username = "reports@example", PasswordEncrypted = "encrypted",
            DefaultClientId = client.Id,
        };
        var api = new ReportSource
        {
            Name = "API", Protocol = "api", Host = null, Port = null, UseTls = null,
            Username = null, PasswordEncrypted = null, DefaultClientId = client.Id,
        };
        db.AddRange(client, mailbox, api);
        db.MailboxSyncRuns.AddRange(
            new MailboxSyncRun
            {
                ReportSourceId = mailbox.Id, Trigger = "scheduled", Status = "success",
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-2), FinishedAtUtc = DateTime.UtcNow,
            },
            new MailboxSyncRun
            {
                ReportSourceId = api.Id, Trigger = "manual", Status = "failed",
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-1), FinishedAtUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var summary = await TestAnalytics.Service(db, TestCurrentUserContext.Admin())
            .GetSummaryAsync(30, default);

        Assert.Equal(1, summary.Mailboxes!.Total);
        Assert.Equal(1, summary.Mailboxes.Healthy);
        Assert.Equal(0, summary.Mailboxes.Failing);
    }

    [Fact]
    public async Task CreateValidatesSourceConfigurationAndListsClientName()
    {
        await using var db = NewDb();
        var service = Service(db);

        Assert.Equal(400, (await service.CreateAsync(new() { Protocol = "smtp" }, default)).StatusCode);
        Assert.Equal(400, (await service.CreateAsync(new() { Protocol = "api" }, default)).StatusCode);
        Assert.Equal(400, (await service.CreateAsync(new()
        {
            Name = "Inbox", Protocol = "imap", DefaultClientId = Guid.NewGuid(),
        }, default)).StatusCode);
        Assert.Equal(400, (await service.CreateAsync(new()
        {
            Name = "Upload", Protocol = "api", DefaultClientId = Guid.NewGuid(), DeleteAfterRetention = true,
        }, default)).StatusCode);
        Assert.Equal(400, (await service.CreateAsync(new()
        {
            Name = "Upload", Protocol = "api", DefaultClientId = Guid.NewGuid(),
        }, default)).StatusCode);

        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var created = await service.CreateAsync(new()
        {
            Name = " Inbox ", Protocol = " IMAP ", Host = " IMAP.EXAMPLE ", Port = 993,
            UseTls = true, Username = " reports@example ", Password = "secret",
            DefaultClientId = client.Id, DeleteAfterRetention = true,
        }, default);

        Assert.True(created.IsSuccess);
        var listed = Assert.Single(await service.ListAsync(default));
        Assert.Equal("Inbox", listed.Name);
        Assert.Equal("imap.example", listed.Host);
        Assert.Equal("Acme", listed.DefaultClientName);
    }

    [Fact]
    public async Task UpdateRejectsInvalidSourceChanges()
    {
        await using var db = NewDb();
        var client = new Client { Name = "Acme", Slug = "acme", Timezone = "UTC" };
        var source = new ReportSource
        {
            Name = "Inbox", Protocol = "imap", Host = "imap.example", Port = 993, UseTls = true,
            Username = "reports@example", PasswordEncrypted = "encrypted", DefaultClientId = client.Id,
        };
        db.AddRange(client, source);
        await db.SaveChangesAsync();
        var service = Service(db);

        Assert.Equal(404, (await service.UpdateAsync(Guid.NewGuid(), new(), default)).StatusCode);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { Protocol = "smtp" }, default)).StatusCode);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { Name = " " }, default)).StatusCode);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { Host = " " }, default)).StatusCode);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { Port = 0 }, default)).StatusCode);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { Username = " " }, default)).StatusCode);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { Password = " " }, default)).StatusCode);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { DefaultClientId = Guid.Empty }, default)).StatusCode);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { DefaultClientId = Guid.NewGuid() }, default)).StatusCode);

        var api = await service.UpdateAsync(source.Id, new() { Protocol = "api" }, default);
        Assert.True(api.IsSuccess);
        Assert.Equal(400, (await service.UpdateAsync(source.Id, new() { DeleteAfterRetention = true }, default)).StatusCode);
    }
}
