using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class SyncRequestQueueTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task Enqueue_Racers_ProduceOneRowReturnedToAll()
    {
        var sourceId = await ResetMigrateAndSeedAsync();

        // Released at once so the enqueues genuinely overlap: some losers see
        // the winner in the pre-check, others hit the partial unique index in
        // the catch path. Every outcome must converge on the one row.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = database.CreateDbContext();
            var service = NewService(db);
            await gate.Task;
            return await service.EnqueueAsync(sourceId, CancellationToken.None);
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(racers);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results.Select(result => result.Value!.RequestId).Distinct());
        Assert.Single(results, result => result.Value!.IsNew);

        await using var verification = database.CreateDbContext();
        var row = Assert.Single(await verification.SyncRequests.ToListAsync());
        Assert.Equal(results[0].Value!.RequestId, row.Id);
        Assert.Equal(SyncRequestStatus.Queued, row.Status);
    }

    [Fact]
    public async Task Claim_Racers_ExactlyOneWins()
    {
        var sourceId = await ResetMigrateAndSeedAsync();

        await using (var db = database.CreateDbContext())
        {
            var enqueued = await NewService(db).EnqueueAsync(sourceId, CancellationToken.None);
            Assert.True(enqueued.Value!.IsNew);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var db = database.CreateDbContext();
            await gate.Task;
            return await NewService(db).ClaimNextAsync(CancellationToken.None);
        })).ToArray();
        gate.SetResult();
        var claims = await Task.WhenAll(racers);

        Assert.Single(claims, claim => claim is not null);
        Assert.Single(claims, claim => claim is null);

        await using var verification = database.CreateDbContext();
        var row = await verification.SyncRequests.SingleAsync();
        Assert.Equal(SyncRequestStatus.Running, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.StartedAtUtc);
    }

    [Fact]
    public async Task Claim_Heartbeat_Complete_LifecycleFreesTheSource()
    {
        var sourceId = await ResetMigrateAndSeedAsync();

        Guid firstId;
        await using (var db = database.CreateDbContext())
        {
            var service = NewService(db);
            firstId = (await service.EnqueueAsync(sourceId, CancellationToken.None)).Value!.RequestId;

            var claim = await service.ClaimNextAsync(CancellationToken.None);
            Assert.NotNull(claim);
            Assert.Equal(firstId, claim.RequestId);
            Assert.Equal(sourceId, claim.ReportSourceId);
            Assert.Null(await service.ClaimNextAsync(CancellationToken.None));

            Assert.True(await service.HeartbeatAsync(firstId, """{"scanned":10}""", CancellationToken.None));
            Assert.True(await service.CompleteAsync(firstId, """{"reportsInserted":3}""", CancellationToken.None));
        }

        await using (var db = database.CreateDbContext())
        {
            var finished = await db.SyncRequests.SingleAsync(x => x.Id == firstId);
            Assert.Equal(SyncRequestStatus.Completed, finished.Status);
            Assert.Equal("""{"reportsInserted":3}""", finished.ResultJson);
            Assert.Equal(1, finished.Attempts);
            Assert.NotNull(finished.StartedAtUtc);
            Assert.NotNull(finished.FinishedAtUtc);

            // The terminal row is outside the partial index, so the source
            // takes a new request while keeping its history.
            var second = await NewService(db).EnqueueAsync(sourceId, CancellationToken.None);
            Assert.True(second.Value!.IsNew);
            Assert.NotEqual(firstId, second.Value.RequestId);
            Assert.Equal(2, await db.SyncRequests.CountAsync());
        }
    }

    [Fact]
    public async Task Fail_And_Partial_WriteTerminalOutcomes()
    {
        var sourceId = await ResetMigrateAndSeedAsync();
        Guid failedId;
        Guid partialId;

        await using (var db = database.CreateDbContext())
        {
            var service = NewService(db);
            failedId = (await service.EnqueueAsync(sourceId, CancellationToken.None)).Value!.RequestId;
            Assert.NotNull(await service.ClaimNextAsync(CancellationToken.None));
            Assert.True(await service.FailAsync(failedId, "connection refused", null, CancellationToken.None));

            partialId = (await service.EnqueueAsync(sourceId, CancellationToken.None)).Value!.RequestId;
            Assert.NotNull(await service.ClaimNextAsync(CancellationToken.None));
            Assert.True(await service.MarkPartialAsync(partialId, """{"resumed":true}""", CancellationToken.None));
        }

        await using var verification = database.CreateDbContext();
        var failed = await verification.SyncRequests.SingleAsync(x => x.Id == failedId);
        Assert.Equal(SyncRequestStatus.Failed, failed.Status);
        Assert.Equal("connection refused", failed.LastError);

        var partial = await verification.SyncRequests.SingleAsync(x => x.Id == partialId);
        Assert.Equal(SyncRequestStatus.Partial, partial.Status);
        Assert.Equal("""{"resumed":true}""", partial.ResultJson);
    }

    private static SyncRequestService NewService(DmarcAnalyzerDbContext db)
        => new(db, new StubTransportFactory(), new SystemUserContext());

    private async Task<Guid> ResetMigrateAndSeedAsync()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var client = new Client
        {
            Name = "Sync queue fixture",
            Slug = $"sync-queue-{Guid.NewGuid():N}",
            Timezone = "UTC",
        };
        var source = new ReportSource
        {
            Name = "Sync queue source",
            Protocol = ReportSourceProtocols.Imap,
            Host = "imap.example",
            Port = 993,
            UseTls = true,
            Username = "reports@example",
            PasswordEncrypted = "secret",
            DefaultClientId = client.Id,
        };

        await using var db = database.CreateDbContext();
        db.AddRange(client, source);
        await db.SaveChangesAsync();

        return source.Id;
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
