namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A request to sync one report source, durable across stateless worker
/// invocations: the requester enqueues, a worker claims it, and the outcome
/// (or partial progress) lands back on the row.
/// <para>
/// Status is one of <c>queued</c>, <c>running</c>, <c>completed</c>,
/// <c>partial</c>, <c>failed</c>, or <c>cancelled</c>. At most one
/// <c>queued</c>/<c>running</c> row per source exists at a time (partial
/// unique index), so a second request while one is in flight is refused at
/// the insert rather than double-syncing the mailbox.
/// </para>
/// </summary>
public sealed class SyncRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReportSourceId { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? RequestedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int Attempts { get; set; }

    /// <summary>Why the last attempt failed; truncated to 2000 chars by writers.</summary>
    public string? LastError { get; set; }

    /// <summary>Outcome payload JSON; truncated to 8000 chars by writers.</summary>
    public string? ResultJson { get; set; }

    public ReportSource? ReportSource { get; set; }
}
