namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

/// <summary>Controls the worker pass that watches each domain's SPF dependencies for drift.</summary>
public sealed class SpfDriftOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gap between drift passes. Twelve hours: dependencies move slowly, and
    /// each check walks DNS widely (analysis plus candidate generation), so
    /// this costs far more per domain than the MTA-STS pass. Cadence versus
    /// dependency TTLs is still an open question (#41) — revisit if TTL-aware
    /// scheduling lands.
    /// </summary>
    public int CheckIntervalHours { get; set; } = 12;

    /// <summary>How many domains are checked concurrently during a pass.</summary>
    public int MaxConcurrentChecks { get; set; } = 4;

    /// <summary>
    /// Wall clock per domain: analysis (20s cap) plus candidate generation
    /// (25s cap) run back to back, so this must clear 45s with margin. A domain
    /// that exceeds it reads as a lookup failure — last-known-good kept.
    /// </summary>
    public int PerDomainTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Each check waits a random 0–N seconds before starting, so a pass over
    /// hundreds of domains does not burst the resolver the moment it begins.
    /// </summary>
    public int StartJitterSeconds { get; set; } = 5;
}
