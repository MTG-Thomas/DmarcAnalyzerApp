namespace DmarcAnalyzer.Api.Workers;

/// <summary>The <c>WorkerOnce:*</c> settings, read only in <c>worker-once</c> mode. Every property here must have a row in docs/ops/configuration.md — a test enforces it.</summary>
public sealed class WorkerOnceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "WorkerOnce";

    /// <summary>
    /// Wall-clock bound on the whole run: lock wait plus every pass. Past it
    /// the run stops and exits nonzero, so an overrunning schedule slot fails
    /// loudly instead of overlapping the next one.
    /// </summary>
    public int OverallTimeoutMinutes { get; set; } = 50;

    /// <summary>
    /// How long to wait for the ingestion lock before giving up. Giving up is
    /// a clean skip that exits zero — another worker owns this slot, so there
    /// is nothing for this run to do — not a failure.
    /// </summary>
    public int LockWaitSeconds { get; set; } = 30;
}
