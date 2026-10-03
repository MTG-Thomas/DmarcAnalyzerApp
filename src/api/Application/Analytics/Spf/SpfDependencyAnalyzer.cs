using System.Text;

namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

/// <summary>Outcome of one node in the SPF dependency tree.</summary>
public static class SpfNodeStatus
{
    public const string Found = "found";
    public const string Missing = "missing";
    public const string LookupFailed = "lookup_failed";

    /// <summary>The record exists but receivers cannot use it (multiple records here).</summary>
    public const string Permerror = "permerror";

    /// <summary>Already on the current include path — followed once, not descended again.</summary>
    public const string Cycle = "cycle";

    /// <summary>Not visited: a bound tripped first (depth, query cap, budget, voids, time).</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// The recursive SPF dependency analysis for one domain: the tree plus the
/// global budget rollup. Discovery, not match evaluation — nothing here decides
/// whether a sender passes, only what a receiver would have to look at.
/// </summary>
public sealed record SpfAnalysis(
    SpfDependencyNodeDto Root,
    /// <summary>RFC 7208 §4.6.4 DNS-causing mechanisms across the whole tree. Receivers permerror past 10.</summary>
    int TotalLookups,
    /// <summary>Mechanism-triggered queries answered empty (NXDOMAIN or no records). Past 2 is a permerror.</summary>
    int VoidLookups,
    bool OverBudget,
    /// <summary>The term that spent lookup 11, when over budget.</summary>
    string? OverBudgetTerm,
    /// <summary>SPF TXT payload bytes actually fetched. Response-size pressure lives here, not in the count.</summary>
    int EstimatedResponseBytes,
    /// <summary>Real DNS queries performed (TXT + MX), against MaxQueries.</summary>
    int QueriesPerformed,
    IReadOnlyList<string> Issues);

public interface ISpfDependencyAnalyzer
{
    /// <summary>
    /// Walk the SPF dependency tree from <paramref name="domainName"/>, bounded on
    /// every axis. Never throws except on cancellation — DNS weirdness comes back
    /// as node outcomes, and anything unexpected degrades to a lookup_failed root
    /// so a read-only enrichment can never fail the inspection card.
    /// </summary>
    Task<SpfAnalysis> AnalyzeAsync(string domainName, CancellationToken ct);
}

/// <summary>
/// Follows include:/redirect= chains the way a receiver would, counting every
/// DNS-causing term against the single RFC 7208 §4.6.4 budget of 10. The top-level
/// count in <see cref="RecordInspectionService.ParseSpf"/> answers "how many here";
/// this answers "how many in total".
/// <para>
/// Bounds: recursion depth, total real queries, MX hosts shown per term, elapsed
/// time, and the two RFC tripwires (10 lookups, 2 void lookups) past which a
/// receiver stops with permerror — discovery stops there too, and says so.
/// </para>
/// </summary>
public sealed class SpfDependencyAnalyzer(IDnsTxtResolver txt, IDnsMxResolver mx) : ISpfDependencyAnalyzer
{
    /// <summary>RFC 7208 §4.6.4: at most 10 DNS-causing mechanisms/redirects per check.</summary>
    private const int RfcLookupLimit = 10;

    /// <summary>RFC 7208 §4.6.4: more than 2 void (empty-answer) lookups is a permerror.</summary>
    private const int MaxVoidLookups = 2;

    /// <summary>Deepest include chain followed. Ten costs ten lookups, so the RFC budget normally trips first.</summary>
    private const int MaxDepth = 10;

    /// <summary>Real DNS queries per analysis. A page load, not a crawl — worst case stays interactive.</summary>
    private const int MaxQueries = 50;

    /// <summary>Exchange hosts shown per mx term; the total is still counted.</summary>
    private const int MaxMxHostsShown = 10;

    /// <summary>Wall clock per analysis. Slow DNS fails open into partial results, never a hung page.</summary>
    private static readonly TimeSpan ElapsedLimit = TimeSpan.FromSeconds(20);

    public async Task<SpfAnalysis> AnalyzeAsync(string domainName, CancellationToken ct)
    {
        var state = new WalkState { StartedUtc = DateTime.UtcNow };
        SpfDependencyNodeDto root;
        try
        {
            root = await VisitAsync(Normalize(domainName), 0, [], state, isRoot: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything unexpected (a resolver throwing despite its contract, a bug in
            // the walk) degrades to one failed node — the card keeps its top-level result.
            root = new SpfDependencyNodeDto(Normalize(domainName), 0, SpfNodeStatus.LookupFailed, null, [], 0,
                [$"SPF analysis failed unexpectedly ({ex.GetType().Name}) — the top-level record above is still accurate."]);
        }

        var issues = new List<string>();
        if (state.OverBudgetTerm is not null)
        {
            issues.Add($"{state.RfcLookups} recursive DNS lookups — receivers permerror past " +
                $"{RfcLookupLimit} (tripped at \"{state.OverBudgetTerm}\").");
        }

        if (state.VoidLimitTripped)
        {
            issues.Add($"{state.Voids} void lookups — receivers permerror past {MaxVoidLookups} empty answers.");
        }

        issues.AddRange(state.Cycles.Select(c => $"Include cycle: {c} — followed once, not descended again."));
        issues.AddRange(state.PermerrorBranches.Select(d => $"{d} is unusable (permerror) — its branch was not followed."));
        issues.AddRange(state.FailedBranches.Select(d => $"{d} could not be checked — its branch is unknown."));

        return new SpfAnalysis(root, state.RfcLookups, state.Voids, state.OverBudgetTerm is not null,
            state.OverBudgetTerm, state.ResponseBytes, state.Queries, issues);
    }

    private async Task<SpfDependencyNodeDto> VisitAsync(
        string domain, int depth, List<string> path, WalkState state, bool isRoot, CancellationToken ct)
    {
        if (DateTime.UtcNow - state.StartedUtc > ElapsedLimit)
        {
            return Skip(domain, depth, "Stopped: the 20s analysis budget ran out first.");
        }

        if (depth > MaxDepth)
        {
            return Skip(domain, depth, "Stopped: deeper than 10 includes.");
        }

        if (state.Aborted)
        {
            return Skip(domain, depth, "Not evaluated — the lookup budget already ran out.");
        }

        // Cycle check before any query: a name already on this path costs nothing to recognize.
        // List, not set: depth is capped at 10, and the cycle report needs path order.
        if (path.Contains(domain))
        {
            var cycle = string.Join(" → ", path.Append(domain));
            if (!state.Cycles.Contains(cycle))
            {
                state.Cycles.Add(cycle);
            }

            return new SpfDependencyNodeDto(domain, depth, SpfNodeStatus.Cycle, null, [], 0,
                [$"{domain} is already being evaluated above — cycle, not followed again."]);
        }

        path.Add(domain);
        try
        {
            if (state.Queries >= MaxQueries)
            {
                return Skip(domain, depth, "Stopped: the 50-query analysis budget ran out first.");
            }

            ct.ThrowIfCancellationRequested();
            var txts = await txt.ResolveAsync(domain, ct);
            state.Queries++;

            if (txts is null)
            {
                if (!isRoot)
                {
                    state.AddFailedBranch(domain);
                }

                return new SpfDependencyNodeDto(domain, depth, SpfNodeStatus.LookupFailed, null, [], 0,
                    [$"DNS lookup for {domain} failed — its branch is unknown."]);
            }

            state.ResponseBytes += txts.Sum(t => Encoding.UTF8.GetByteCount(t));

            var records = txts.Where(t => t.TrimStart().StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
            if (records.Count == 0)
            {
                return new SpfDependencyNodeDto(domain, depth, SpfNodeStatus.Missing, null, [], 0,
                    [$"{domain} publishes no SPF record."]);
            }

            if (records.Count > 1)
            {
                // RFC 7208 §3.2: multiple records are a permerror — receivers evaluate none
                // of them. Parse the first for display so the fix is visible, follow nothing.
                if (!isRoot)
                {
                    state.AddPermerrorBranch(domain);
                }

                var first = SpfRecordParser.Parse(records[0]);
                return new SpfDependencyNodeDto(domain, depth, SpfNodeStatus.Permerror, records[0],
                    first.Terms.Select(t => ToTermDto(t, null, [], 0)).ToList(), 0,
                    [$"{records.Count} SPF records at {domain} — receivers reject all of them (permerror)."]);
            }

            var before = state.RfcLookups;
            var record = SpfRecordParser.Parse(records[0]);
            var terms = await EvaluateTermsAsync(domain, depth, path, record, state, ct);
            var nodeIssues = new List<string>();
            if (record.Terms.Count(t => t.Kind == SpfTermKind.Redirect) > 1)
            {
                nodeIssues.Add("More than one redirect= — receivers follow only the first.");
            }

            return new SpfDependencyNodeDto(domain, depth, SpfNodeStatus.Found, records[0], terms,
                state.RfcLookups - before, nodeIssues);
        }
        finally
        {
            path.Remove(domain);
        }
    }

    private async Task<IReadOnlyList<SpfTermDto>> EvaluateTermsAsync(
        string domain, int depth, List<string> path, SpfRecord record, WalkState state, CancellationToken ct)
    {
        // redirect= delegates the whole check: local mechanisms are ignored, only the
        // target is followed. An unfollowable redirect (macro, IP, empty) is reported
        // and the mechanisms are evaluated normally instead.
        var redirect = FollowableRedirect(record);
        var result = new List<SpfTermDto>();
        var sawAll = false;
        var sawRedirect = false;

        foreach (var term in record.Terms)
        {
            if (term.ParseError is not null)
            {
                result.Add(ToTermDto(term, $"Unparseable — receivers permerror here: {term.ParseError}", [], 0));
                continue;
            }

            if (term.Kind == SpfTermKind.All)
            {
                // all matches every sender, so evaluation always stops here —
                // anything after it never costs a lookup.
                sawAll = true;
                result.Add(ToTermDto(term, NoteForFreeTerm(term), [], 0));
                continue;
            }

            if (!term.CostsLookup)
            {
                result.Add(ToTermDto(term, NoteForFreeTerm(term), [], 0));
                continue;
            }

            if (redirect is not null && term.Kind != SpfTermKind.Redirect)
            {
                result.Add(ToTermDto(term, "Ignored — redirect= delegates evaluation to another record.", [], 0));
                continue;
            }

            if (sawAll)
            {
                result.Add(ToTermDto(term, "Unreachable — evaluation stops at all.", [], 0));
                continue;
            }

            if (state.Aborted)
            {
                result.Add(ToTermDto(term, "Not evaluated — the lookup budget already ran out.", [], 0));
                continue;
            }

            if (term.Kind == SpfTermKind.Redirect)
            {
                if (sawRedirect)
                {
                    result.Add(ToTermDto(term, "Ignored — only the first redirect= is followed.", [], 0));
                    continue;
                }

                sawRedirect = true;
            }

            state.RfcLookups++;
            if (state.RfcLookups > RfcLookupLimit)
            {
                state.OverBudgetTerm ??= term.Text;
                result.Add(ToTermDto(term, $"Lookup {state.RfcLookups} of {RfcLookupLimit} — receivers stop here (permerror).", [], 0));
                continue;
            }

            result.Add(await EvaluateCostingTermAsync(domain, depth, path, term, state, ct));
        }

        return result;
    }

    private async Task<SpfTermDto> EvaluateCostingTermAsync(
        string domain, int depth, List<string> path, SpfTerm term, WalkState state, CancellationToken ct)
    {
        switch (term.Kind)
        {
            case SpfTermKind.Include:
            case SpfTermKind.Redirect:
            {
                var target = term.Target!;
                if (term.HasMacro)
                {
                    return ToTermDto(term, "Macro in the target — unexpandable without a sender identity, not followed.", [], 0);
                }

                if (IsIpLiteral(target))
                {
                    return ToTermDto(term, "Target is an IP address, not a domain — not followed.", [], 0);
                }

                var child = await VisitAsync(Normalize(target), depth + 1, path, state, isRoot: false, ct);
                if (child.Status == SpfNodeStatus.Missing)
                {
                    // An include/redirect answered empty is a void lookup (RFC 7208 §4.6.4).
                    state.Voids++;
                    if (state.Voids > MaxVoidLookups)
                    {
                        state.VoidLimitTripped = true;
                    }
                }

                return ToTermDto(term, null, child);
            }

            case SpfTermKind.Mx:
            {
                if (term.HasMacro)
                {
                    return ToTermDto(term, "Macro in the target — unexpandable without a sender identity.", [], 0);
                }

                if (term.Target is not null && IsIpLiteral(term.Target))
                {
                    return ToTermDto(term, "Target is an IP address, not a domain — no MX to list.", [], 0);
                }

                if (state.Queries >= MaxQueries)
                {
                    return ToTermDto(term, "MX hosts not listed — the 50-query analysis budget ran out first.", [], 0);
                }

                var target = term.Target is null ? domain : Normalize(term.Target);
                var hosts = await mx.ResolveAsync(target, ct);
                state.Queries++;

                if (hosts is null)
                {
                    state.AddFailedBranch(target);
                    return ToTermDto(term, $"MX lookup for {target} failed — hosts unknown.", [], 0);
                }

                if (hosts.Count == 0)
                {
                    state.Voids++;
                    if (state.Voids > MaxVoidLookups)
                    {
                        state.VoidLimitTripped = true;
                    }

                    return ToTermDto(term, $"No MX records at {target} — void lookup.", [], 0);
                }

                var shown = hosts.Take(MaxMxHostsShown).Select(h => h.Host).ToList();
                return ToTermDto(term, null, resolution: null, mxHosts: shown, mxHostTotal: hosts.Count);
            }

            case SpfTermKind.A:
                return ToTermDto(term, term.Target is null
                    ? "Checked against the domain's own A records at send time — not expanded here."
                    : $"Checked against {term.Target}'s A records at send time — not expanded here.", [], 0);

            case SpfTermKind.Ptr:
                return ToTermDto(term, "ptr is deprecated and slow — counted toward the budget, never expanded.", [], 0);

            case SpfTermKind.Exists:
                return ToTermDto(term, "Dynamic by design (DNS existence test) — counted, never expanded.", [], 0);

            default:
                return ToTermDto(term, null, [], 0);
        }
    }

    /// <summary>
    /// The redirect target when it can actually be followed: present, non-empty,
    /// macro-free, and not an IP literal.
    /// </summary>
    private static string? FollowableRedirect(SpfRecord record)
    {
        var target = record.RedirectTarget;
        if (string.IsNullOrWhiteSpace(target) || target.Contains("%{", StringComparison.Ordinal) || IsIpLiteral(target))
        {
            return null;
        }

        return target;
    }

    private static string? NoteForFreeTerm(SpfTerm term) => term.Kind switch
    {
        SpfTermKind.Exp => "Explanation text only — fetched only when a message fails.",
        SpfTermKind.UnknownModifier => "Unknown modifier — receivers ignore it.",
        _ => null,
    };

    private static SpfTermDto ToTermDto(
        SpfTerm term, string? note, SpfDependencyNodeDto? resolution,
        IReadOnlyList<string>? mxHosts = null, int mxHostTotal = 0)
        => new(term.Text, term.Kind, term.Qualifier, term.Target, term.CostsLookup, term.IsDynamic,
            note ?? term.ParseError, resolution, mxHosts ?? [], mxHostTotal);

    private static SpfTermDto ToTermDto(SpfTerm term, string? note, IReadOnlyList<string> mxHosts, int mxHostTotal)
        => ToTermDto(term, note, null, mxHosts, mxHostTotal);

    private static SpfDependencyNodeDto Skip(string domain, int depth, string reason)
        => new(domain, depth, SpfNodeStatus.Skipped, null, [], 0, [reason]);

    private static string Normalize(string domain)
        => domain.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool IsIpLiteral(string value)
        => System.Net.IPAddress.TryParse(value.Trim().TrimEnd('.'), out _);

    /// <summary>Mutable walk state: the two RFC counters plus every analysis bound.</summary>
    private sealed class WalkState
    {
        public DateTime StartedUtc;
        public int RfcLookups;
        public int Voids;
        public int Queries;
        public int ResponseBytes;
        public string? OverBudgetTerm;
        public bool VoidLimitTripped;
        public bool Aborted => OverBudgetTerm is not null || VoidLimitTripped;
        public List<string> Cycles { get; } = [];
        public List<string> PermerrorBranches { get; } = [];
        public List<string> FailedBranches { get; } = [];

        // Diamonds revisit the same branch down each path — report each once.
        public void AddPermerrorBranch(string domain)
        {
            if (!PermerrorBranches.Contains(domain))
            {
                PermerrorBranches.Add(domain);
            }
        }

        public void AddFailedBranch(string domain)
        {
            if (!FailedBranches.Contains(domain))
            {
                FailedBranches.Add(domain);
            }
        }
    }
}
