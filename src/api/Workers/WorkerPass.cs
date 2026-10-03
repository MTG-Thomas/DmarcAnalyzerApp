namespace DmarcAnalyzer.Api.Workers;

/// <summary>
/// One named step of a worker iteration: closing stale syncs, the scheduled
/// sync, or one of the periodic maintenance passes.
/// </summary>
/// <param name="Name">Short stable name, used in logs and the run-once outcome.</param>
/// <param name="Run">The pass itself.</param>
public sealed record WorkerPass(string Name, Func<CancellationToken, Task> Run);
