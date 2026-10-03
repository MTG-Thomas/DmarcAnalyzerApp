using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class SyncRequestServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static SyncRequestService NewService(DmarcAnalyzerDbContext db, ICurrentUserContext user)
        => new(db, new StubTransportFactory(), user);

    [Fact]
    public async Task Enqueue_CreatesQueuedRequest()
    {
        await using var db = NewDb();
        var user = TestCurrentUserContext.Admin();
        var sourceId = await SeedMailboxSourceAsync(db);

        var result = await NewService(db, user).EnqueueAsync(sourceId, default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsNew);
        Assert.Equal(SyncRequestStatus.Queued, result.Value.Status);

        var row = await db.SyncRequests.SingleAsync();
        Assert.Equal(result.Value.RequestId, row.Id);
        Assert.Equal(sourceId, row.ReportSourceId);
        Assert.Equal(SyncRequestStatus.Queued, row.Status);
        Assert.Equal(user.UserId, row.RequestedByUserId);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.StartedAtUtc);
        Assert.Null(row.FinishedAtUtc);
    }

    [Fact]
    public async Task Enqueue_UnknownSource_Returns404()
    {
        await using var db = NewDb();

        var result = await NewService(db, TestCurrentUserContext.Admin())
            .EnqueueAsync(Guid.NewGuid(), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task Enqueue_CrossTenantViewer_Returns404ButGrantedViewerSucceeds()
    {
        await using var db = NewDb();
        var sourceId = await SeedMailboxSourceAsync(db);
        var otherClientId = Guid.NewGuid();

        var denied = await NewService(db, TestCurrentUserContext.Viewer(otherClientId))
            .EnqueueAsync(sourceId, default);
        Assert.False(denied.IsSuccess);
        Assert.Equal(404, denied.StatusCode);
        Assert.Empty(await db.SyncRequests.ToListAsync());

        var source = await db.ReportSources.SingleAsync(x => x.Id == sourceId);
        var granted = await NewService(db, TestCurrentUserContext.Viewer(source.DefaultClientId))
            .EnqueueAsync(sourceId, default);
        Assert.True(granted.IsSuccess);
    }

    [Fact]
    public async Task Enqueue_ApiSource_Returns400()
    {
        await using var db = NewDb();
        var sourceId = await SeedApiSourceAsync(db);

        var result = await NewService(db, TestCurrentUserContext.Admin())
            .EnqueueAsync(sourceId, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.Empty(await db.SyncRequests.ToListAsync());
    }

    [Fact]
    public async Task Enqueue_IncompleteMailbox_Returns409()
    {
        await using var db = NewDb();
        var sourceId = await SeedIncompleteMailboxSourceAsync(db);

        var result = await NewService(db, TestCurrentUserContext.Admin())
            .EnqueueAsync(sourceId, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.StatusCode);
        Assert.Empty(await db.SyncRequests.ToListAsync());
    }

    [Fact]
    public async Task Enqueue_ServiceCaller_LeavesRequesterNull()
    {
        await using var db = NewDb();
        var sourceId = await SeedMailboxSourceAsync(db);
        var service = new TestCurrentUserContext
        {
            ActorType = "service",
            Role = Roles.AgencyAnalyst,
            ServicePermissions = [ServiceApiPermissions.SourcesSync],
        };

        var result = await NewService(db, service).EnqueueAsync(sourceId, default);

        // The service identity's UserId is the credential id, not a user row —
        // recording it as the requester would point at nothing.
        Assert.True(result.IsSuccess);
        Assert.Null((await db.SyncRequests.SingleAsync()).RequestedByUserId);
    }

    [Fact]
    public async Task Enqueue_Dedupe_ReturnsExistingQueuedOrRunningRequest()
    {
        await using var db = NewDb();
        var service = NewService(db, TestCurrentUserContext.Admin());
        var sourceId = await SeedMailboxSourceAsync(db);

        var first = await service.EnqueueAsync(sourceId, default);
        var second = await service.EnqueueAsync(sourceId, default);

        Assert.True(first.Value!.IsNew);
        Assert.False(second.Value!.IsNew);
        Assert.Equal(first.Value.RequestId, second.Value.RequestId);
        Assert.Equal(SyncRequestStatus.Queued, second.Value.Status);
        Assert.Equal(1, await db.SyncRequests.CountAsync());

        // The dedupe covers running rows too, not just queued ones.
        var row = await db.SyncRequests.SingleAsync();
        row.Status = SyncRequestStatus.Running;
        await db.SaveChangesAsync();

        var third = await service.EnqueueAsync(sourceId, default);
        Assert.False(third.Value!.IsNew);
        Assert.Equal(first.Value.RequestId, third.Value.RequestId);
        Assert.Equal(SyncRequestStatus.Running, third.Value.Status);
        Assert.Equal(1, await db.SyncRequests.CountAsync());
    }

    [Fact]
    public async Task Enqueue_AfterTerminal_CreatesNewRequest()
    {
        await using var db = NewDb();
        var service = NewService(db, TestCurrentUserContext.Admin());
        var sourceId = await SeedMailboxSourceAsync(db);

        var first = await service.EnqueueAsync(sourceId, default);
        Assert.True(await service.CompleteAsync(first.Value!.RequestId, "{}", default));

        var second = await service.EnqueueAsync(sourceId, default);

        Assert.True(second.Value!.IsNew);
        Assert.NotEqual(first.Value.RequestId, second.Value.RequestId);
        Assert.Equal(2, await db.SyncRequests.CountAsync());
    }

    [Fact]
    public async Task Get_ReturnsDetails()
    {
        await using var db = NewDb();
        var service = NewService(db, TestCurrentUserContext.Admin());
        var sourceId = await SeedMailboxSourceAsync(db);
        var enqueued = await service.EnqueueAsync(sourceId, default);

        var result = await service.GetAsync(enqueued.Value!.RequestId, default);

        Assert.True(result.IsSuccess);
        var details = result.Value!;
        Assert.Equal(enqueued.Value.RequestId, details.RequestId);
        Assert.Equal(sourceId, details.ReportSourceId);
        Assert.Equal(SyncRequestStatus.Queued, details.Status);
        Assert.Equal(0, details.Attempts);
        Assert.Null(details.StartedAtUtc);
        Assert.Null(details.FinishedAtUtc);
        Assert.Null(details.Error);
        Assert.Null(details.Summary);
    }

    [Fact]
    public async Task Get_UnknownOrCrossTenant_Returns404()
    {
        await using var db = NewDb();
        var sourceId = await SeedMailboxSourceAsync(db);
        var admin = NewService(db, TestCurrentUserContext.Admin());
        var enqueued = await admin.EnqueueAsync(sourceId, default);

        Assert.Equal(404, (await admin.GetAsync(Guid.NewGuid(), default)).StatusCode);

        var denied = await NewService(db, TestCurrentUserContext.Viewer(Guid.NewGuid()))
            .GetAsync(enqueued.Value!.RequestId, default);
        Assert.False(denied.IsSuccess);
        Assert.Equal(404, denied.StatusCode);
    }

    [Fact]
    public async Task Claim_ThenComplete_Lifecycle()
    {
        await using var db = NewDb();
        var service = NewService(db, TestCurrentUserContext.Admin());
        var sourceId = await SeedMailboxSourceAsync(db);
        var enqueued = await service.EnqueueAsync(sourceId, default);

        var claim = await service.ClaimNextAsync(default);

        Assert.NotNull(claim);
        Assert.Equal(enqueued.Value!.RequestId, claim.RequestId);
        Assert.Equal(sourceId, claim.ReportSourceId);
        Assert.Equal(1, claim.Attempts);
        Assert.Null(await service.ClaimNextAsync(default));

        var claimed = await db.SyncRequests.SingleAsync();
        Assert.Equal(SyncRequestStatus.Running, claimed.Status);
        Assert.NotNull(claimed.StartedAtUtc);

        Assert.True(await service.HeartbeatAsync(claim.RequestId, """{"scanned":10}""", default));
        Assert.Equal("""{"scanned":10}""", (await db.SyncRequests.SingleAsync()).ResultJson);

        Assert.True(await service.CompleteAsync(claim.RequestId, """{"reportsInserted":3}""", default));

        var finished = await service.GetAsync(claim.RequestId, default);
        Assert.Equal(SyncRequestStatus.Completed, finished.Value!.Status);
        Assert.Equal("""{"reportsInserted":3}""", finished.Value.Summary);
        Assert.NotNull(finished.Value.FinishedAtUtc);

        // Terminal rows stay terminal: a retried finish is a no-op, not an error.
        Assert.False(await service.CompleteAsync(claim.RequestId, "{}", default));
        Assert.False(await service.FailAsync(claim.RequestId, "boom", null, default));
        Assert.False(await service.HeartbeatAsync(claim.RequestId, null, default));
    }

    [Fact]
    public async Task Fail_SetsFailedWithTruncatedError()
    {
        await using var db = NewDb();
        var service = NewService(db, TestCurrentUserContext.Admin());
        var sourceId = await SeedMailboxSourceAsync(db);
        var enqueued = await service.EnqueueAsync(sourceId, default);
        Assert.NotNull(await service.ClaimNextAsync(default));

        Assert.True(await service.FailAsync(
            enqueued.Value!.RequestId, new string('e', 5000), new string('r', 9000), default));

        var details = (await service.GetAsync(enqueued.Value.RequestId, default)).Value!;
        Assert.Equal(SyncRequestStatus.Failed, details.Status);
        Assert.Equal(2000, details.Error!.Length);
        Assert.Equal(8000, details.Summary!.Length);
        Assert.NotNull(details.FinishedAtUtc);
    }

    [Fact]
    public async Task MarkPartial_SetsPartial()
    {
        await using var db = NewDb();
        var service = NewService(db, TestCurrentUserContext.Admin());
        var sourceId = await SeedMailboxSourceAsync(db);
        var enqueued = await service.EnqueueAsync(sourceId, default);
        Assert.NotNull(await service.ClaimNextAsync(default));

        Assert.True(await service.MarkPartialAsync(enqueued.Value!.RequestId, """{"resumed":true}""", default));

        var details = (await service.GetAsync(enqueued.Value.RequestId, default)).Value!;
        Assert.Equal(SyncRequestStatus.Partial, details.Status);
        Assert.Equal("""{"resumed":true}""", details.Summary);
    }

    [Fact]
    public async Task Heartbeat_QueuedOrUnknown_ReturnsFalse()
    {
        await using var db = NewDb();
        var service = NewService(db, TestCurrentUserContext.Admin());
        var sourceId = await SeedMailboxSourceAsync(db);
        var enqueued = await service.EnqueueAsync(sourceId, default);

        Assert.False(await service.HeartbeatAsync(enqueued.Value!.RequestId, null, default));
        Assert.False(await service.HeartbeatAsync(Guid.NewGuid(), null, default));
        Assert.False(await service.CompleteAsync(Guid.NewGuid(), null, default));
    }

    private static async Task<Guid> SeedMailboxSourceAsync(DmarcAnalyzerDbContext db)
    {
        var client = NewClient();
        var source = new ReportSource
        {
            Name = "Mailbox fixture",
            Protocol = ReportSourceProtocols.Imap,
            Host = "imap.example",
            Port = 993,
            UseTls = true,
            Username = "reports@example",
            PasswordEncrypted = "secret",
            DefaultClientId = client.Id,
        };
        db.AddRange(client, source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private static async Task<Guid> SeedApiSourceAsync(DmarcAnalyzerDbContext db)
    {
        var client = NewClient();
        var source = new ReportSource
        {
            Name = "API fixture",
            Protocol = ReportSourceProtocols.Api,
            DefaultClientId = client.Id,
        };
        db.AddRange(client, source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private static async Task<Guid> SeedIncompleteMailboxSourceAsync(DmarcAnalyzerDbContext db)
    {
        var client = NewClient();
        var source = new ReportSource
        {
            Name = "Incomplete fixture",
            Protocol = ReportSourceProtocols.Imap,
            Host = "imap.example",
            Port = 993,
            UseTls = true,
            Username = "reports@example",
            PasswordEncrypted = null,
            DefaultClientId = client.Id,
        };
        db.AddRange(client, source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private static Client NewClient() => new()
    {
        Name = "Sync request fixture",
        Slug = $"sync-request-{Guid.NewGuid():N}",
        Timezone = "UTC",
    };

    private sealed class StubTransportFactory : IPolledSourceTransportFactory
    {
        public IPolledSourceTransport? For(string protocol)
            => ReportSourceProtocols.IsPolled(protocol) ? new StubTransport(protocol) : null;

        private sealed class StubTransport(string protocol) : IPolledSourceTransport
        {
            public string Protocol => protocol;

            public Task<IPolledReadSession> OpenForReadAsync(
                ReportSource source, string secret, CancellationToken ct)
                => throw new NotSupportedException();

            public Task<IPolledPruneSession> OpenForPruneAsync(
                ReportSource source, string secret, DateTime cutoffUtc, bool dryRun, CancellationToken ct)
                => throw new NotSupportedException();
        }
    }
}
