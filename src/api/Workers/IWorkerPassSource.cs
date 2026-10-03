namespace DmarcAnalyzer.Api.Workers;

/// <summary>
/// The worker's passes as named steps. The loop runs them in order and treats
/// any throw as a failed iteration; a run-once invocation runs the same list
/// with per-pass isolation so one throwing pass cannot skip the later ones.
/// </summary>
public interface IWorkerPassSource
{
    /// <summary>The passes in run order. The list is fixed; each call returns the same sequence.</summary>
    IReadOnlyList<WorkerPass> GetPasses();
}
