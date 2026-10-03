using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Workers;

/// <summary>
/// Runs the worker's passes exactly once and reports an exit code.
/// <para>
/// Exit 0 means the slot needed nothing more from this run: every pass ran
/// (<c>complete</c>), or another worker owned the slot and this run skipped it
/// (<c>lock-contention</c>). Anything else is 1: a pass threw
/// (<c>pass-failures</c>), the run outlived its bound (<c>timeout</c>), it was
/// cancelled (<c>cancelled</c>), or the lock itself errored
/// (<c>lock-error</c>). Failures absorbed inside a pass — a source that would
/// not sync, a report that would not parse — are the pass completing, not the
/// run failing, so they stay 0.
/// </para>
/// <para>
/// Passes run isolated from each other: one throwing pass is recorded and the
/// run continues with the next, so a single failure cannot silently skip every
/// pass after it the way it would in the loop. Every path logs one structured
/// outcome line, so the orchestrator's log answers what happened without
/// correlating per-pass lines.
/// </para>
/// </summary>
public sealed class WorkerOnceRunner(
    IWorkerPassSource passes,
    IWorkerInstanceLock workerLock,
    IOptions<WorkerOnceOptions> options,
    ILogger<WorkerOnceRunner> logger)
{
    /// <summary>Process exit codes for <c>worker-once</c> mode.</summary>
    public static class ExitCodes
    {
        /// <summary>Complete, or skipped because another worker owns the slot.</summary>
        public const int Success = 0;

        /// <summary>A pass threw, the run timed out or was cancelled, or the lock errored.</summary>
        public const int RequiredWorkFailed = 1;
    }

    /// <summary>Runs every pass once and returns the process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var startedUtc = DateTime.UtcNow;
        var overallTimeout = TimeSpan.FromMinutes(options.Value.OverallTimeoutMinutes);

        using var timeoutCts = new CancellationTokenSource();
        if (overallTimeout <= TimeSpan.Zero)
        {
            timeoutCts.Cancel();
        }
        else
        {
            timeoutCts.CancelAfter(overallTimeout);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var ct = linkedCts.Token;

        bool acquired;
        try
        {
            acquired = await WaitForLockAsync(TimeSpan.FromSeconds(options.Value.LockWaitSeconds), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Finish(CancelledOutcome(timeoutCts), startedUtc, total: 0, failedPasses: []);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "worker-once could not take the ingestion lock");
            return Finish("lock-error", startedUtc, total: 0, failedPasses: []);
        }

        if (!acquired)
        {
            // Not a failure: another worker is running these passes, so this
            // slot has nothing left to do. Zero keeps the scheduler quiet.
            return Finish("lock-contention", startedUtc, total: 0, failedPasses: []);
        }

        var passList = passes.GetPasses();
        var failedPasses = new List<string>();
        foreach (var pass in passList)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await pass.Run(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failedPasses.Add(pass.Name);
                logger.LogError(
                    ex,
                    "worker-once pass {Pass} failed; continuing with the remaining passes",
                    pass.Name);
            }
        }

        if (ct.IsCancellationRequested)
        {
            return Finish(CancelledOutcome(timeoutCts), startedUtc, passList.Count, failedPasses);
        }

        return failedPasses.Count == 0
            ? Finish("complete", startedUtc, passList.Count, failedPasses)
            : Finish("pass-failures", startedUtc, passList.Count, failedPasses);
    }

    private static string CancelledOutcome(CancellationTokenSource timeoutCts)
        => timeoutCts.IsCancellationRequested ? "timeout" : "cancelled";

    private async Task<bool> WaitForLockAsync(TimeSpan lockWait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + lockWait;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await workerLock.TryAcquireAsync(ct))
            {
                return true;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(
                remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining,
                ct);
        }
    }

    private int Finish(string outcome, DateTime startedUtc, int total, List<string> failedPasses)
    {
        var exitCode = outcome is "complete" or "lock-contention"
            ? ExitCodes.Success
            : ExitCodes.RequiredWorkFailed;

        logger.LogInformation(
            "worker-once finished: outcome={Outcome} passes={Total} failed={FailedCount} " +
            "failedPasses={FailedPasses} durationMs={DurationMs} exitCode={ExitCode}",
            outcome,
            total,
            failedPasses.Count,
            failedPasses.Count == 0 ? "(none)" : string.Join(",", failedPasses),
            (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds,
            exitCode);

        return exitCode;
    }
}
