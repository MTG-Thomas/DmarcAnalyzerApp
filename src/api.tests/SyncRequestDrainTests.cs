using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The sync-request drain pass, driven through the public pass list so the
/// test covers what both the loop and worker-once execute: claim each queued
/// request, run one sync, write the outcome back.
/// </summary>
public sealed class SyncRequestDrainTests
{
    private static ServiceProvider NewProvider(
        string dbName, FakeMailboxSyncService sync, ICurrentUserContext user)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        services.AddScoped<ISyncRequestService, SyncRequestService>();
        services.AddSingleton<IPolledSourceTransportFactory>(new StubTransportFactory());
        services.AddSingleton(user);
        services.AddSingleton<IMailboxSyncService>(sync);
        return services.BuildServiceProvider();
    }

    private static QueueWorkerService NewWorker(IServiceScopeFactory scopes)
        => new(
            scopes,
            new WorkerSingleInstanceLock(
                new ConfigurationBuilder().Build(),
                Options.Create(new WorkerOptions()),
                NullLogger<WorkerSingleInstanceLock>.Instance),
            Options.Create(new WorkerOptions()),
            Options.Create(new BackupOptions()),
            NullLogger<QueueWorkerService>.Instance);

    private static async Task<Guid> SeedSourceAsync(DmarcAnalyzerDbContext db)
    {
        var client = new Client
        {
            Name = "Drain fixture",
            Slug = $"drain-{Guid.NewGuid():N}",
            Timezone = "UTC",
        };
        var source = new ReportSource
        {
            Name = "drain.example",
            Protocol = ReportSourceProtocols.Imap,
            Host = "imap.example",
            Port = 993,
            UseTls = true,
            Username = "reports@example",
            PasswordEncrypted = "x",
            DefaultClientId = client.Id,
        };
        db.AddRange(client, source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private static MailboxSyncResult SyncOk(Guid sourceId) => new(
        sourceId, MessagesScanned: 4, AttachmentsProcessed: 2, ReportsInserted: 2,
        ReportsSkippedAsDuplicate: 0, TlsReportsInserted: 0, TlsReportsSkippedAsDuplicate: 0,
        ParseFailures: 0, Success: true, Error: null,
        StartedAtUtc: DateTime.UtcNow, FinishedAtUtc: DateTime.UtcNow);

    [Fact]
    public async Task Drain_CompletesQueuedRequest_WithOutcomeCounters()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var sync = new FakeMailboxSyncService();
        await using var provider = NewProvider(dbName, sync, TestCurrentUserContext.Admin());
        Guid sourceId, requestId;
        using (var seed = provider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            sourceId = await SeedSourceAsync(db);
            var enqueue = await seed.ServiceProvider.GetRequiredService<ISyncRequestService>()
                .EnqueueAsync(sourceId, default);
            requestId = enqueue.Value!.RequestId;
        }

        sync.Next = id => ServiceResult<MailboxSyncResult>.Success(SyncOk(id));
        var worker = NewWorker(provider.GetRequiredService<IServiceScopeFactory>());
        var drain = worker.GetPasses().Single(p => p.Name == "sync-request-drain");
        await drain.Run(default);

        using var verify = provider.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>()
            .SyncRequests.SingleAsync(x => x.Id == requestId);
        Assert.Equal(SyncRequestStatus.Completed, row.Status);
        Assert.Contains("\"reportsInserted\":2", row.ResultJson, StringComparison.Ordinal);
        Assert.Single(sync.Synced, sourceId);
    }

    [Fact]
    public async Task Drain_FailedSync_FailsTheRequest_WithOutcome()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var sync = new FakeMailboxSyncService();
        await using var provider = NewProvider(dbName, sync, TestCurrentUserContext.Admin());
        Guid requestId;
        using (var seed = provider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            var sourceId = await SeedSourceAsync(db);
            var enqueue = await seed.ServiceProvider.GetRequiredService<ISyncRequestService>()
                .EnqueueAsync(sourceId, default);
            requestId = enqueue.Value!.RequestId;
        }

        sync.Next = id => ServiceResult<MailboxSyncResult>.Success(
            SyncOk(id) with { Success = false, Error = "mailbox unreachable" });
        var worker = NewWorker(provider.GetRequiredService<IServiceScopeFactory>());
        await worker.GetPasses().Single(p => p.Name == "sync-request-drain").Run(default);

        using var verify = provider.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>()
            .SyncRequests.SingleAsync(x => x.Id == requestId);
        Assert.Equal(SyncRequestStatus.Failed, row.Status);
        Assert.Equal("mailbox unreachable", row.LastError);
    }

    [Fact]
    public async Task Drain_CrashedSync_FailsTheRequest_AndContinues()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var sync = new FakeMailboxSyncService();
        await using var provider = NewProvider(dbName, sync, TestCurrentUserContext.Admin());
        using (var seed = provider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            var services = seed.ServiceProvider.GetRequiredService<ISyncRequestService>();
            var first = await SeedSourceAsync(db);
            var secondClient = new Client
            {
                Name = "Second",
                Slug = $"second-{Guid.NewGuid():N}",
                Timezone = "UTC",
            };
            var second = new ReportSource
            {
                Name = "second.example",
                Protocol = ReportSourceProtocols.Imap,
                Host = "imap.example",
                Port = 993,
                UseTls = true,
                Username = "reports@example",
                PasswordEncrypted = "x",
                DefaultClientId = secondClient.Id,
            };
            db.AddRange(secondClient, second);
            await db.SaveChangesAsync();
            await services.EnqueueAsync(first, default);
            await services.EnqueueAsync(second.Id, default);
        }

        var calls = 0;
        sync.Next = id =>
        {
            calls++;
            return calls == 1
                ? throw new InvalidOperationException("boom")
                : ServiceResult<MailboxSyncResult>.Success(SyncOk(id));
        };
        var worker = NewWorker(provider.GetRequiredService<IServiceScopeFactory>());
        await worker.GetPasses().Single(p => p.Name == "sync-request-drain").Run(default);

        using var verify = provider.CreateScope();
        var rows = await verify.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>()
            .SyncRequests.ToListAsync();
        Assert.Equal(2, calls);
        Assert.Contains(rows, r => r.Status == SyncRequestStatus.Failed);
        Assert.Contains(rows, r => r.Status == SyncRequestStatus.Completed);
    }

    [Fact]
    public async Task Drain_RecoversAbandonedRunning_BeforeClaiming()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var sync = new FakeMailboxSyncService();
        await using var provider = NewProvider(dbName, sync, TestCurrentUserContext.Admin());
        Guid requestId;
        using (var seed = provider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            var sourceId = await SeedSourceAsync(db);
            var request = new SyncRequest
            {
                ReportSourceId = sourceId,
                Status = SyncRequestStatus.Running,
                StartedAtUtc = DateTime.UtcNow.AddHours(-2),
                Attempts = 1,
            };
            db.SyncRequests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.Id;
        }

        sync.Next = id => ServiceResult<MailboxSyncResult>.Success(SyncOk(id));
        var worker = NewWorker(provider.GetRequiredService<IServiceScopeFactory>());
        await worker.GetPasses().Single(p => p.Name == "sync-request-drain").Run(default);

        using var verify = provider.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>()
            .SyncRequests.SingleAsync(x => x.Id == requestId);
        Assert.Equal(SyncRequestStatus.Completed, row.Status);
    }

    [Fact]
    public async Task Drain_EmptyQueue_CostsOneRead_AndSyncsNothing()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var sync = new FakeMailboxSyncService();
        await using var provider = NewProvider(dbName, sync, TestCurrentUserContext.Admin());
        using (var seed = provider.CreateScope())
        {
            await SeedSourceAsync(seed.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        }

        var worker = NewWorker(provider.GetRequiredService<IServiceScopeFactory>());
        await worker.GetPasses().Single(p => p.Name == "sync-request-drain").Run(default);

        Assert.Empty(sync.Synced);
    }

    private sealed class FakeMailboxSyncService : IMailboxSyncService
    {
        public Func<Guid, ServiceResult<MailboxSyncResult>> Next { get; set; } =
            id => ServiceResult<MailboxSyncResult>.Success(new MailboxSyncResult(
                id, 0, 0, 0, 0, 0, 0, 0, true, null, DateTime.UtcNow, DateTime.UtcNow));

        public List<Guid> Synced { get; } = [];

        public Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, CancellationToken ct)
            => SyncReportSourceAsync(reportSourceId, "manual", ct);

        public Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(
            Guid reportSourceId, string trigger, CancellationToken ct)
        {
            Synced.Add(reportSourceId);
            return Task.FromResult(Next(reportSourceId));
        }
    }

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
