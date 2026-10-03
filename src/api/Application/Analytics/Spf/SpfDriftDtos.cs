namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

/// <summary>
/// The persisted SPF drift state of a domain, as the console renders it.
/// Checked is false (and every nullable field null, every list empty) for a
/// domain the pass has not reached yet — distinct from missing, which is a
/// definitive answer.
/// </summary>
public sealed record SpfDriftStateDto(
    Guid DomainId,
    string Name,
    bool Checked,
    string? SpfRecordStatus,
    string? RawRecord,
    IReadOnlyList<SpfDependencySnapshotEntry> Dependencies,
    IReadOnlyList<SpfDependencySnapshotEntry> PreviousDependencies,
    DateTime? DependencyChangedAtUtc,
    string? CandidateStatus,
    string? CandidateText,
    string? PreviousCandidateStatus,
    string? PreviousCandidateText,
    DateTime? CandidateChangedAtUtc,
    int? PublishedLookups,
    int? CandidateLookups,
    int? CandidateLength,
    bool? PublishedOverBudget,
    int? PreviousPublishedLookups,
    int? PreviousCandidateLookups,
    int? PreviousCandidateLength,
    bool? PreviousPublishedOverBudget,
    IReadOnlyList<string> Issues,
    DateTime? LastCheckedAtUtc,
    DateTime? LastChangedAtUtc,
    DateTime? LastSuccessAtUtc,
    int? ConsecutiveFailures);
