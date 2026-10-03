namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>Outcome of a live DNS check: found, missing, or the lookup itself failed.</summary>
public static class RecordLookupStatus
{
    /// <summary>The domain publishes its own record.</summary>
    public const string Found = "found";

    /// <summary>
    /// No record here, but an ancestor publishes one, so a receiver applies that. Distinct
    /// from Found because the policy is not this domain's to change, and from Missing because
    /// the domain is not unprotected.
    /// </summary>
    public const string Inherited = "inherited";

    /// <summary>Definitively not published — unlike <see cref="LookupFailed"/>, this is an answer.</summary>
    public const string Missing = "missing";

    /// <summary>The DNS query itself failed (timeout/servfail) — says nothing about the record.</summary>
    public const string LookupFailed = "lookup_failed";
}

/// <summary>The live DMARC record at _dmarc.{domain}, parsed tag by tag.</summary>
/// <param name="Testing">Testing mode (RFC 9989) — y/n, null when not published. Default is n.</param>
/// <param name="PublicSuffixDomain">Public suffix domain flag (RFC 9989) — y/n/u, null when not published. Default is u.</param>
/// <param name="NonExistentSubdomainPolicy">Policy for non-existent subdomains (RFC 9989, promoted from experimental RFC 9091).</param>
public sealed record DnsDmarcRecordDto(
    string Status,
    string? Raw,
    string? Policy,
    string? SubdomainPolicy,
    int? Pct,
    string? Rua,
    string? Ruf,
    string? DkimAlignment,
    string? SpfAlignment,
    IReadOnlyList<string> Issues,
    string? Testing = null,
    string? PublicSuffixDomain = null,
    string? NonExistentSubdomainPolicy = null);

/// <summary>
/// The live SPF record(s) at {domain}. LookupMechanisms counts top-level
/// mechanisms that cost a DNS lookup (include/a/mx/ptr/exists/redirect) —
/// RFC 7208 caps the resolved total at 10, which is what RecursiveLookups reports.
/// </summary>
/// <param name="RecursiveLookups">DNS-causing mechanisms across the whole include tree. Receivers permerror past 10.</param>
/// <param name="VoidLookups">Mechanism-triggered queries answered empty. Past 2 is a permerror.</param>
/// <param name="OverBudget">True when the recursive walk spent more than 10 lookups.</param>
/// <param name="EstimatedResponseBytes">SPF TXT payload bytes fetched during the walk. Null when nothing was walked.</param>
/// <param name="DependencyTree">The recursive dependency tree, null unless Status is found.</param>
public sealed record DnsSpfRecordDto(
    string Status,
    string? Raw,
    int RecordCount,
    int LookupMechanisms,
    string? AllQualifier,
    IReadOnlyList<string> Issues,
    int RecursiveLookups = 0,
    int VoidLookups = 0,
    bool OverBudget = false,
    int? EstimatedResponseBytes = null,
    SpfDependencyNodeDto? DependencyTree = null);

/// <summary>One parsed SPF term inside a dependency-tree node, with its resolution attached.</summary>
/// <param name="Text">The term exactly as published.</param>
/// <param name="Kind">all/include/a/mx/ptr/exists/ip4/ip6/redirect/exp/unknown_modifier/invalid.</param>
/// <param name="Qualifier">One of + - ~ ?. Default + when the term carries none.</param>
/// <param name="Target">domain-spec for include/redirect/a/mx/ptr/exists, address for ip4/ip6, else null.</param>
/// <param name="CostsLookup">Counts toward the RFC 7208 limit of 10 DNS-causing mechanisms.</param>
/// <param name="IsDynamic">Authorization set unknowable statically: macros, or exists: (DNS-by-design).</param>
/// <param name="Note">Why this term was not or could not be followed, null when it needs no note.</param>
/// <param name="Resolution">The followed include/redirect target, null for anything not followed.</param>
/// <param name="MxHosts">MX exchange hosts for an mx term, capped; empty for every other kind.</param>
/// <param name="MxHostTotal">How many MX hosts exist in total (more than shown when capped).</param>
public sealed record SpfTermDto(
    string Text,
    string Kind,
    string Qualifier,
    string? Target,
    bool CostsLookup,
    bool IsDynamic,
    string? Note,
    SpfDependencyNodeDto? Resolution,
    IReadOnlyList<string> MxHosts,
    int MxHostTotal);

/// <summary>One SPF record in the recursive dependency tree.</summary>
/// <param name="Status">found/missing/lookup_failed/permerror/cycle/skipped.</param>
/// <param name="LookupsUsed">RFC lookups spent in this subtree, including nested includes.</param>
public sealed record SpfDependencyNodeDto(
    string Domain,
    int Depth,
    string Status,
    string? Raw,
    IReadOnlyList<SpfTermDto> Terms,
    int LookupsUsed,
    IReadOnlyList<string> Issues);

/// <summary>The DMARC policy reporters most recently observed (policy_published).</summary>
public sealed record ObservedPolicyDto(
    string Policy,
    string? SubdomainPolicy,
    int Pct,
    string DkimAlignment,
    string SpfAlignment,
    DateTime AsOfUtc,
    string ReportedBy);

/// <summary>How a published tag lines up with what the reporter echoed back.</summary>
public static class RecordComparisonStatus
{
    /// <summary>Published and observed agree.</summary>
    public const string Match = "match";

    /// <summary>Reporters echo a different value — usually a propagating or regional DNS change.</summary>
    public const string Differs = "differs";

    /// <summary>Not published, so RFC 7489 derives it — nothing to disagree with.</summary>
    public const string Inherited = "inherited";

    /// <summary>Published, but the reporter sent no value for it.</summary>
    public const string NotReported = "not_reported";
}

/// <summary>
/// One published-vs-observed field comparison. Only <see cref="RecordComparisonStatus.Differs"/>
/// is a finding; the other three states are informational.
/// </summary>
public sealed record RecordComparisonDto(
    string Field,
    string? Published,
    string? Observed,
    string Status,
    string? Note = null);

/// <summary>Whether a rua/ruf destination outside this domain has authorized receiving its reports.</summary>
public static class ExternalDestinationAuthStatus
{
    /// <summary>The destination publishes the {domain}._report._dmarc.{destination} opt-in record.</summary>
    public const string Authorized = "authorized";

    /// <summary>No opt-in record — conforming receivers silently drop reports sent there.</summary>
    public const string NotAuthorized = "not_authorized";

    /// <summary>The authorization check itself failed — not evidence either way.</summary>
    public const string LookupFailed = "lookup_failed";
}

/// <summary>
/// A rua/ruf address at a domain other than the one publishing the DMARC record only
/// works if that destination opts in, by publishing a DMARC record at
/// {domain}._report._dmarc.{destination} (RFC 9990 §4). Without it, conforming
/// receivers silently drop the reports — nothing bounces to say why.
/// </summary>
public sealed record ExternalDestinationAuthDto(
    string Destination,
    string Status,
    string Detail);

/// <summary>
/// The record-inspection card: live DMARC and SPF records, the policy reporters
/// last observed, field-by-field comparison, and external rua/ruf authorization.
/// </summary>
public sealed record RecordInspectionDto(
    Guid DomainId,
    string Name,
    DnsDmarcRecordDto Dmarc,
    DnsSpfRecordDto Spf,
    ObservedPolicyDto? Observed,
    IReadOnlyList<RecordComparisonDto> Comparison,
    IReadOnlyList<ExternalDestinationAuthDto> ExternalDestinations);
