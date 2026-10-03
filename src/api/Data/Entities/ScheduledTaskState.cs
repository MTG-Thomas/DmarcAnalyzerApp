namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// Durable per-task scheduler state for the serverless worker: when a named
/// task last ran and last succeeded, and how many times it has failed since.
/// <para>
/// Rows are lazy-created on first run; <see cref="TaskKey"/> is free text so
/// new tasks need no schema change. The consecutive-failure counter is what a
/// stateless host cannot keep in memory — it is the backoff and alert signal.
/// </para>
/// </summary>
public sealed class ScheduledTaskState
{
    public string TaskKey { get; set; } = string.Empty;
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
