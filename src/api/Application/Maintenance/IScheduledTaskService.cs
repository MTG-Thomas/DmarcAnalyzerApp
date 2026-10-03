namespace DmarcAnalyzer.Api.Application.Maintenance;

/// <summary>
/// Durable due-check for the worker's periodic maintenance passes. Where the
/// worker used to keep last-run timestamps in memory (so every restart re-ran
/// every pass), the cadence now lives in <c>scheduled_task_state</c> and
/// survives restarts. The due check, the run, and the record all happen inside
/// the worker's ownership lock, so a restart can never re-run a fresh task.
/// </summary>
public interface IScheduledTaskService
{
    /// <summary>
    /// True when the task has never run or its last run is at least
    /// <paramref name="interval"/> old. A task with no row is always due.
    /// Read-only: rows are lazy-created by <see cref="RecordRunAsync"/>.
    /// </summary>
    Task<bool> IsDueAsync(string taskKey, TimeSpan interval, CancellationToken ct = default);

    /// <summary>
    /// Records a run, lazy-creating the row. Always advances the last-run
    /// clock (so a recorded failure still waits out the interval); a success
    /// also stamps last-success and resets the consecutive-failure counter.
    /// Callers record exactly where they used to advance an in-memory
    /// timestamp, which keeps failure-retry behavior identical.
    /// </summary>
    Task RecordRunAsync(string taskKey, bool success, CancellationToken ct = default);
}
