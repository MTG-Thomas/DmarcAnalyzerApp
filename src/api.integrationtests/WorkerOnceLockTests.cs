using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
public sealed class WorkerOnceLockTests(PostgreSqlDatabaseFixture database)
{
    private WorkerSingleInstanceLock CreateLock(string name)
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = name,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection.ConnectionString }).Build();
        return new WorkerSingleInstanceLock(configuration, Options.Create(new WorkerOptions()),
            NullLogger<WorkerSingleInstanceLock>.Instance);
    }

    private WorkerOnceRunner CreateRunner(IWorkerPassSource passes, IWorkerInstanceLock workerLock, int lockWaitSeconds)
        => new(passes, workerLock,
            Options.Create(new WorkerOnceOptions { LockWaitSeconds = lockWaitSeconds }),
            NullLogger<WorkerOnceRunner>.Instance);

    [Fact]
    public async Task TryAcquire_ReturnsFalseWhileHeldAndTrueAfterRelease()
    {
        await using var owner = CreateLock("once-try-owner");
        await owner.AcquireAsync(default);
        await using var contender = CreateLock("once-try-contender");

        Assert.False(await contender.TryAcquireAsync(default));

        await owner.DisposeAsync();

        Assert.True(await contender.TryAcquireAsync(default));
    }

    [Fact]
    public async Task TwoConcurrentWorkers_ExactlyOneRunsAndTheOtherSkipsCleanly()
    {
        await using var owner = CreateLock("once-race-owner");
        await owner.AcquireAsync(default);

        // The second worker arrives while the first holds the lock: it must
        // skip its slot with a zero exit, running no pass at all.
        var skipped = new RecordingPasses();
        await using var contender = CreateLock("once-race-contender");
        var skipRunner = CreateRunner(skipped, contender, lockWaitSeconds: 1);

        var skipExit = await skipRunner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.Success, skipExit);
        Assert.Empty(skipped.Ran);

        // Once the owner is gone the same slot runs exactly once.
        await owner.DisposeAsync();
        var ran = new RecordingPasses();
        await using var next = CreateLock("once-race-next");
        var runRunner = CreateRunner(ran, next, lockWaitSeconds: 5);

        var runExit = await runRunner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.Success, runExit);
        Assert.Equal(["only"], ran.Ran);
    }

    private sealed class RecordingPasses : IWorkerPassSource
    {
        public List<string> Ran { get; } = [];

        public IReadOnlyList<WorkerPass> GetPasses()
            => [new("only", ct =>
            {
                Ran.Add("only");
                return Task.CompletedTask;
            })];
    }
}
