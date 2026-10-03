namespace DmarcAnalyzer.Api.Workers;

/// <summary>
/// Exclusive ingestion ownership: at most one worker runs passes against a
/// database. The loop waits for it; a run-once invocation only tries.
/// </summary>
public interface IWorkerInstanceLock
{
    /// <summary>
    /// Blocks until this process owns the lock. For the long-lived loop,
    /// which has nothing better to do than wait for the previous owner.
    /// </summary>
    Task AcquireAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns immediately: true when this process now owns the lock, false
    /// when another worker holds it. For run-once invocations, which would
    /// rather skip their slot than queue behind the owner.
    /// </summary>
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken);
}
