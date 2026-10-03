namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// The current SPF drift posture of a domain, as last observed by the worker's
/// drift pass (or an on-demand recheck): the live SPF record, a snapshot of
/// every dependency target's published record, and the flattening candidate
/// that snapshot yields.
/// <para>
/// One row per domain, current state only — no history. Dependency changes are
/// tracked with a single previous snapshot/hash, which is what the alert
/// evaluator and the reviewable diff need; anything longer-lived belongs in
/// alert_event.
/// </para>
/// <para>
/// Like the MTA-STS state row, a failed lookup keeps the last known values
/// rather than blanking them: a transient SERVFAIL must not read as "no SPF"
/// or silently retire a candidate. Only a definitive <c>missing</c> clears
/// them. Excluded from the backup config artifact — a cache the pass rebuilds
/// within one interval.
/// </para>
/// </summary>
public sealed class SpfDriftState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }

    /// <summary>
    /// Outcome of the domain's SPF TXT lookup: found, missing, lookup_failed,
    /// or invalid (two or more v=spf1 records — receivers permerror, so there
    /// is no usable record to snapshot or flatten).
    /// </summary>
    public string SpfRecordStatus { get; set; } = string.Empty;

    /// <summary>The SPF TXT record as published, when exactly one was found.</summary>
    public string? RawRecord { get; set; }

    /// <summary>
    /// JSON array of {domain, record}: every include/redirect target the
    /// recursive walk resolved, each with the record it published at check
    /// time (null when that target's lookup failed). The reviewable snapshot
    /// a drift diff is computed from.
    /// </summary>
    public string? DependencySnapshotJson { get; set; }

    /// <summary>SHA-256 over the normalized snapshot (plus the root record) — change detection.</summary>
    public string? DependencyHash { get; set; }

    /// <summary>The hash before the last observed change; null until a change has been seen.</summary>
    public string? PreviousDependencyHash { get; set; }

    /// <summary>Previous snapshot JSON, kept so the console can render old→new per target.</summary>
    public string? PreviousDependencySnapshotJson { get; set; }

    /// <summary>When the dependency hash last moved (both sides non-null); the alert window.</summary>
    public DateTime? DependencyChangedAtUtc { get; set; }

    /// <summary>ready/refused from the last candidate generation; null when no record was found.</summary>
    public string? CandidateStatus { get; set; }

    /// <summary>The last generated candidate text; null when refused or unchecked.</summary>
    public string? CandidateText { get; set; }

    /// <summary>SHA-256 of the candidate text; null when refused or unchecked.</summary>
    public string? CandidateHash { get; set; }

    /// <summary>The candidate status before the last safety flip; null until one has been seen.</summary>
    public string? PreviousCandidateStatus { get; set; }

    /// <summary>When the candidate status last flipped; the safety alert window.</summary>
    public DateTime? CandidateChangedAtUtc { get; set; }

    /// <summary>Recursive RFC lookups the published record costs (the honest "before").</summary>
    public int? PublishedLookups { get; set; }

    /// <summary>RFC lookups the candidate still costs (preserved stubs).</summary>
    public int? CandidateLookups { get; set; }

    /// <summary>Candidate characters (0 when refused).</summary>
    public int? CandidateLength { get; set; }

    /// <summary>Whether the published tree exceeds the 10-lookup budget senders enforce.</summary>
    public bool? PublishedOverBudget { get; set; }

    /// <summary>JSON string array of findings from the last check (unfollowed branches, budget notes), ready to render.</summary>
    public string? IssuesJson { get; set; }

    /// <summary>
    /// Previous values, set when the dependency hash moves, so the drift alert
    /// and the reviewable diff can say what changed: lookups, size, budget and
    /// the candidate text itself. Segments derive from length, so they travel
    /// in the DTO, not the row.
    /// </summary>
    public int? PreviousPublishedLookups { get; set; }

    public int? PreviousCandidateLookups { get; set; }

    public int? PreviousCandidateLength { get; set; }

    public bool? PreviousPublishedOverBudget { get; set; }

    public string? PreviousCandidateText { get; set; }

    public string? PreviousCandidateHash { get; set; }

    /// <summary>
    /// When a full check last succeeded. Never cleared — together with
    /// <see cref="ConsecutiveFailures"/> this is the freshness signal: unknown
    /// while failures stack up, stale when the last success ages out.
    /// </summary>
    public DateTime? LastSuccessAtUtc { get; set; }

    /// <summary>Failures since the last success; reset to zero by it.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Always advanced by a check, even when nothing moved — "we verified this" is what it is for.</summary>
    public DateTime LastCheckedAtUtc { get; set; }

    /// <summary>When any material field last changed; drives "last verified" copy.</summary>
    public DateTime? LastChangedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Domain? Domain { get; set; }
}
