using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Exit semantics of a run-once invocation: zero when the slot needed nothing
/// more, nonzero only when required work failed. The passes and the lock are
/// fakes — the real pass list and the real lock are covered where they live.
/// </summary>
public sealed class WorkerOnceRunnerTests
{
    private static WorkerOnceRunner Runner(
        IWorkerPassSource passes,
        IWorkerInstanceLock workerLock,
        WorkerOnceOptions options,
        RecordingLogger? logger = null)
        => new(passes, workerLock, Options.Create(options), logger ?? new RecordingLogger());

    [Fact]
    public async Task AllPassesSucceed_ReturnsZeroAndLogsComplete()
    {
        var passes = new FakePasses("a", "b", "c");
        var logger = new RecordingLogger();
        var runner = Runner(passes, FakeLock.Acquired(), new WorkerOnceOptions(), logger);

        var exitCode = await runner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.Success, exitCode);
        Assert.Equal(["a", "b", "c"], passes.Ran);
        Assert.Contains(logger.Messages, m => m.Contains("outcome=complete") && m.Contains("exitCode=0"));
    }

    [Fact]
    public async Task PassThatAbsorbsItsOwnFailure_ReturnsZero()
    {
        // A source that would not sync, a report that would not parse: the pass
        // completes, so the run succeeded. Only an escaping throw fails it.
        var passes = new FakePasses(
            new WorkerPass("scheduled-sync", _ => Task.CompletedTask));
        var runner = Runner(passes, FakeLock.Acquired(), new WorkerOnceOptions());

        var exitCode = await runner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ThrowingPass_DoesNotSkipLaterPasses_ReturnsOne()
    {
        var passes = new FakePasses(
            new WorkerPass("first", _ => Task.CompletedTask),
            new WorkerPass("broken", _ => throw new InvalidOperationException("boom")),
            new WorkerPass("last", _ => Task.CompletedTask));
        var logger = new RecordingLogger();
        var runner = Runner(passes, FakeLock.Acquired(), new WorkerOnceOptions(), logger);

        var exitCode = await runner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.RequiredWorkFailed, exitCode);
        Assert.Equal(["first", "last"], passes.Ran);
        Assert.Contains(logger.Messages, m =>
            m.Contains("outcome=pass-failures") && m.Contains("broken") && m.Contains("exitCode=1"));
    }

    [Fact]
    public async Task LockContention_SkipsWithoutRunningPasses_ReturnsZero()
    {
        var passes = new FakePasses("a", "b");
        var logger = new RecordingLogger();
        var runner = Runner(
            passes,
            FakeLock.Contended(),
            new WorkerOnceOptions { LockWaitSeconds = 0 },
            logger);

        var exitCode = await runner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.Success, exitCode);
        Assert.Empty(passes.Ran);
        Assert.Contains(logger.Messages, m =>
            m.Contains("outcome=lock-contention") && m.Contains("exitCode=0"));
    }

    [Fact]
    public async Task LockAcquiredAfterRetry_RunsPasses()
    {
        var passes = new FakePasses("a");
        var runner = Runner(
            passes,
            FakeLock.ThenAcquired(attemptsBeforeSuccess: 2),
            new WorkerOnceOptions { LockWaitSeconds = 10 });

        var exitCode = await runner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.Success, exitCode);
        Assert.Equal(["a"], passes.Ran);
    }

    [Fact]
    public async Task OverallTimeoutExceeded_ReturnsOneWithoutRunningPasses()
    {
        var passes = new FakePasses("a");
        var logger = new RecordingLogger();
        var runner = Runner(
            passes,
            FakeLock.Acquired(),
            new WorkerOnceOptions { OverallTimeoutMinutes = 0 },
            logger);

        var exitCode = await runner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.RequiredWorkFailed, exitCode);
        Assert.Empty(passes.Ran);
        Assert.Contains(logger.Messages, m =>
            m.Contains("outcome=timeout") && m.Contains("exitCode=1"));
    }

    [Fact]
    public async Task CancelledWhileRunning_ReturnsOne()
    {
        var passes = new FakePasses(
            new WorkerPass("slow", async ct =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var logger = new RecordingLogger();
        var runner = Runner(passes, FakeLock.Acquired(), new WorkerOnceOptions(), logger);

        var exitCode = await runner.RunAsync(cts.Token);

        Assert.Equal(WorkerOnceRunner.ExitCodes.RequiredWorkFailed, exitCode);
        Assert.Contains(logger.Messages, m =>
            m.Contains("outcome=cancelled") && m.Contains("exitCode=1"));
    }

    [Fact]
    public async Task LockError_ReturnsOne()
    {
        var passes = new FakePasses("a");
        var logger = new RecordingLogger();
        var runner = Runner(
            passes,
            FakeLock.Throwing(new InvalidOperationException("connection refused")),
            new WorkerOnceOptions(),
            logger);

        var exitCode = await runner.RunAsync();

        Assert.Equal(WorkerOnceRunner.ExitCodes.RequiredWorkFailed, exitCode);
        Assert.Empty(passes.Ran);
        Assert.Contains(logger.Messages, m =>
            m.Contains("outcome=lock-error") && m.Contains("exitCode=1"));
    }

    private sealed class FakePasses(params WorkerPass[] passes) : IWorkerPassSource
    {
        public FakePasses(params string[] names)
            : this(names.Select(n => new WorkerPass(n, _ => Task.CompletedTask)).ToArray())
        {
        }

        public List<string> Ran { get; } = [];

        public IReadOnlyList<WorkerPass> GetPasses()
            => passes.Select(p => new WorkerPass(p.Name, async ct =>
            {
                await p.Run(ct);
                Ran.Add(p.Name);
            })).ToList();
    }

    private sealed class FakeLock(Func<CancellationToken, Task<bool>> tryAcquire) : IWorkerInstanceLock
    {
        public static FakeLock Acquired() => new(_ => Task.FromResult(true));

        public static FakeLock Contended() => new(_ => Task.FromResult(false));

        public static FakeLock ThenAcquired(int attemptsBeforeSuccess)
        {
            var attempts = 0;
            return new(_ => Task.FromResult(++attempts > attemptsBeforeSuccess));
        }

        public static FakeLock Throwing(Exception ex) => new(_ => throw ex);

        public Task AcquireAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
            => tryAcquire(cancellationToken);
    }

    private sealed class RecordingLogger : ILogger<WorkerOnceRunner>
    {
        public List<string> Messages { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
