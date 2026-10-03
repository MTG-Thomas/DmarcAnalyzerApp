using System.Net;

namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

/// <summary>Candidate outcome: a publishable record, or a refusal with reasons.</summary>
public static class SpfCandidateStatus
{
    public const string Ready = "ready";
    public const string Refused = "refused";
}

/// <summary>What happened to one top-level term of the published record.</summary>
public static class SpfCandidateTermOutcome
{
    /// <summary>Replaced by its static expansion (expanded terms listed).</summary>
    public const string Expanded = "expanded";

    /// <summary>Verbatim in the candidate, unchanged by nature (ip4/ip6/all/modifiers).</summary>
    public const string Passthrough = "passthrough";

    /// <summary>Kept verbatim with a reason: dynamic, stubbed, unverified, or capped.</summary>
    public const string Preserved = "preserved";

    /// <summary>Left out with a reason: dead (unreachable/ignored) — dropping preserves semantics.</summary>
    public const string Dropped = "dropped";
}

public interface ISpfCandidateGenerator
{
    /// <summary>
    /// Build a conservative flattened SPF candidate for <paramref name="domainName"/>:
    /// static includes expanded to addresses, everything dynamic kept verbatim as
    /// stub terms, dead terms dropped. Never throws except on cancellation.
    /// </summary>
    Task<SpfCandidateDto> GenerateAsync(string domainName, CancellationToken ct, bool bypassCache = false);
}

/// <summary>
/// Flattens an SPF record the boring way: every mechanism that resolves to a
/// static address set becomes ip4:/ip6: terms; everything else — dynamic terms
/// (macros, exists:, ptr), stubbed volatile providers, unverifiable branches —
/// stays verbatim so the candidate never authorizes less (or more) than the
/// published record. All-or-nothing per term: a partially resolvable include is
/// kept whole, never half-expanded.
/// <para>
/// Exactness rules, each load-bearing:
/// <list type="bullet">
/// <item>Term order is preserved — evaluation stops at the first match, so
/// reordering overlapping terms would change results. Expansions splice in
/// place; only addresses within one splice sort (numerically, for determinism).</item>
/// <item>No supernetting — aggregates could authorize unlisted hosts. Dedupe is
/// exact-text only, first occurrence wins.</item>
/// <item>Non-+ qualifiers gate expansion: -include:/~include: stay whole (fail
/// semantics cannot splice), while -mx/-a expand with the qualifier applied
/// per address. Nested non-+ terms can never yield a pass through the include,
/// so they drop with a note.</item>
/// <item>Unknown modifiers and nested explanations drop (ignored by receivers by
/// definition); the effective all qualifier and exp survive.</item>
/// <item>redirect= delegates the whole record — local terms drop, the target's
/// effective content wins.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SpfCandidateGenerator(
    IDnsTxtResolver txt,
    IDnsMxResolver mx,
    IDnsAddressResolver addresses,
    ISpfDependencyAnalyzer analyzer) : ISpfCandidateGenerator
{
    /// <summary>Deepest include/redirect chain followed during expansion.</summary>
    private const int MaxDepth = 10;

    /// <summary>Real DNS queries per candidate. Expansion fans wider than discovery (MX×A per host).</summary>
    private const int MaxQueries = 100;

    /// <summary>MX exchanges expanded per mx term; beyond this the term is preserved, not half-expanded.</summary>
    private const int MaxMxExchanges = 25;

    /// <summary>Expanded addresses per candidate; beyond this the candidate refuses (unpublishable).</summary>
    private const int MaxExpandedIps = 1000;

    /// <summary>Wall clock per candidate. Slow DNS preserves remaining branches, never hangs the page.</summary>
    private static readonly TimeSpan ElapsedLimit = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Includes never expanded, kept as stubs: large providers whose published
    /// address sets churn faster than any flattening stays valid. Static for #43;
    /// #44 drift monitoring is the natural place to observe (not expand) them.
    /// </summary>
    private static readonly HashSet<string> StubProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "_spf.google.com",
        "spf.protection.outlook.com",
    };

    public async Task<SpfCandidateDto> GenerateAsync(string domainName, CancellationToken ct, bool bypassCache = false)
    {
        var state = new WalkState { StartedUtc = DateTime.UtcNow };
        try
        {
            // The recursive count is the honest "before" — the analyzer walks the
            // same tree with the same bounds, and the shared DNS cache makes the
            // second walk nearly free.
            var analysis = await analyzer.AnalyzeAsync(domainName, ct, bypassCache);
            var records = await FetchSpfRecordsAsync(Normalize(domainName), state, bypassCache, ct);
            if (records is null)
            {
                return Refuse(string.Empty, analysis.TotalLookups,
                    ["Could not read the published SPF record — DNS lookup failed."]);
            }

            if (records.Count == 0)
            {
                return Refuse(string.Empty, analysis.TotalLookups,
                    ["No SPF record published — nothing to flatten."]);
            }

            if (records.Count > 1)
            {
                return Refuse(string.Empty, analysis.TotalLookups,
                    [$"{records.Count} SPF records published — receivers reject all of them; fix that first."]);
            }

            var record = SpfRecordParser.Parse(records[0]);
            var expansion = await ExpandRecordAsync(Normalize(domainName), record,
                depth: 0, path: [Normalize(domainName)], state, isEffectiveRoot: true, bypassCache, ct);

            if (expansion.ExpandedIpCount > MaxExpandedIps)
            {
                return Refuse(records[0], analysis.TotalLookups,
                    [$"Expansion yields {expansion.ExpandedIpCount} addresses — too large to publish usefully."]);
            }

            var candidate = Assemble(expansion);
            if (candidate == NormalizeRecord(records[0]))
            {
                var kept = expansion.TermOutcomes
                    .Where(o => o.Outcome == SpfCandidateTermOutcome.Preserved)
                    .Select(o => $"{o.OriginalText} — {o.Reason}")
                    .ToList();
                if (expansion.Failures.Count == 0 && kept.Count == 0)
                {
                    return Refuse(records[0], analysis.TotalLookups,
                        ["Already flat — every term is static and there is nothing to expand."],
                        expansion.TermOutcomes);
                }

                return Refuse(records[0], analysis.TotalLookups,
                    ["Nothing could be expanded.", ..expansion.Failures, ..kept],
                    expansion.TermOutcomes);
            }

            var after = SpfRecordParser.Parse(candidate).Terms.Count(t => t.CostsLookup);
            return new SpfCandidateDto(
                SpfCandidateStatus.Ready, records[0], candidate, expansion.TermOutcomes,
                [], analysis.TotalLookups, after, candidate.Length, Segments(candidate.Length));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SpfCandidateDto(SpfCandidateStatus.Refused, string.Empty, null, [],
                [$"Candidate generation failed unexpectedly ({ex.GetType().Name})."], 0, 0, 0, 0);
        }
    }

    private static SpfCandidateDto Refuse(
        string original, int beforeLookups, IReadOnlyList<string> reasons,
        IReadOnlyList<SpfCandidateTermDto>? terms = null)
        => new(SpfCandidateStatus.Refused, original, null, terms ?? [], reasons, beforeLookups, 0, 0, 0);

    /// <summary>
    /// Expand one record's terms in order, splicing expansions in place.
    /// <paramref name="isEffectiveRoot"/> marks the record whose all/modifiers land
    /// in the candidate — the published record, or the final redirect target.
    /// Nested records contribute only addresses and preserved terms.
    /// </summary>
    private async Task<RecordExpansion> ExpandRecordAsync(
        string domain, SpfRecord record, int depth, List<string> path,
        WalkState state, bool isEffectiveRoot, bool bypassCache, CancellationToken ct)
    {
        var expansion = new RecordExpansion();
        var redirect = FollowableRedirect(record);
        var sawAll = false;
        var sawRedirect = false;

        foreach (var term in record.Terms)
        {
            if (term.ParseError is not null)
            {
                // Unparseable terms are permerror today and permerror in the
                // candidate — kept so the breakage stays visible, not silently fixed.
                expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                    SpfCandidateTermOutcome.Preserved,
                    $"Unparseable — kept as-is, still broken: {term.ParseError}", []));
                continue;
            }

            if (redirect is not null && term.Kind != SpfTermKind.Redirect)
            {
                // redirect= delegates the whole check: every local term, including
                // all and ip4/ip6, is ignored by receivers — dropping preserves that.
                expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
                    SpfCandidateTermOutcome.Dropped, "Ignored — redirect= delegates evaluation.", []));
                continue;
            }

            if (term.Kind == SpfTermKind.All)
            {
                if (sawAll)
                {
                    expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
                        SpfCandidateTermOutcome.Dropped, "Duplicate all — unreachable.", []));
                    continue;
                }

                sawAll = true;
                if (isEffectiveRoot)
                {
                    expansion.AllQualifier = term.Qualifier;
                }

                continue;
            }

            if (term.Kind is SpfTermKind.Exp or SpfTermKind.UnknownModifier)
            {
                // Explanations and ignored modifiers survive only on the effective
                // record, in place; nested copies are meaningless once spliced.
                if (isEffectiveRoot)
                {
                    expansion.OrderedTerms.Add(term.Text);
                }

                continue;
            }

            if (term.Kind is SpfTermKind.Ip4 or SpfTermKind.Ip6)
            {
                if (!isEffectiveRoot && term.Qualifier != "+")
                {
                    // A nested -/~/? address can never yield a pass through the
                    // include — dropped, counted for the include's note.
                    expansion.DroppedUnmatchable++;
                    continue;
                }

                // Qualifier verbatim: -ip4 at the top level keeps its fail result.
                expansion.OrderedTerms.Add(term.Text);
                expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
                    SpfCandidateTermOutcome.Passthrough, null, [term.Text]));
                continue;
            }

            if (!isEffectiveRoot && term.Qualifier != "+")
            {
                // A nested non-+ term can never yield a pass through the include,
                // so it can never affect the outer match — dropped, and counted so
                // the include's note can say so.
                expansion.DroppedUnmatchable++;
                continue;
            }

            if (sawAll)
            {
                expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
                    SpfCandidateTermOutcome.Dropped, "Unreachable — evaluation stops at all.", []));
                continue;
            }

            if (term.Kind == SpfTermKind.Redirect)
            {
                if (sawRedirect)
                {
                    expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
                        SpfCandidateTermOutcome.Dropped, "Only the first redirect= is followed.", []));
                    continue;
                }

                sawRedirect = true;
            }

            await ExpandMechanismAsync(domain, term, depth, path, expansion, state, isEffectiveRoot, bypassCache, ct);
        }

        return expansion;
    }

    private async Task ExpandMechanismAsync(
        string domain, SpfTerm term, int depth, List<string> path,
        RecordExpansion expansion, WalkState state, bool isEffectiveRoot, bool bypassCache,
        CancellationToken ct)
    {
        if (OutOfBudget(state))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Not expanded — the analysis budget ran out first.", []));
            return;
        }

        // Fail semantics cannot splice: -include:/~include: match with a result the
        // flattened addresses could not reproduce, so they stay whole. Bare/+ only.
        if (term.Kind == SpfTermKind.Include && term.Qualifier != "+")
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved,
                $"Qualified include ({term.Qualifier}) — fail semantics kept verbatim, not expanded.", []));
            return;
        }

        switch (term.Kind)
        {
            case SpfTermKind.Include:
                await ExpandIncludeAsync(term, depth, path, expansion, state, bypassCache, ct);
                break;

            case SpfTermKind.Redirect:
                await ExpandRedirectAsync(term, depth, path, expansion, state, isEffectiveRoot, bypassCache, ct);
                break;

            case SpfTermKind.Mx:
                await ExpandMxAsync(domain, term, expansion, state, bypassCache, ct);
                break;

            case SpfTermKind.A:
                await ExpandHostAsync(domain, term, expansion, state, bypassCache, ct);
                break;

            case SpfTermKind.Ptr:
                expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                    SpfCandidateTermOutcome.Preserved,
                    "ptr depends on the sender's reverse DNS — cannot be flattened, kept.", []));
                break;

            case SpfTermKind.Exists:
                expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                    SpfCandidateTermOutcome.Preserved,
                    "Dynamic DNS existence test — cannot be flattened, kept.", []));
                break;

            default:
                expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                    SpfCandidateTermOutcome.Preserved, "Not expandable — kept as-is.", []));
                break;
        }
    }

    private async Task ExpandIncludeAsync(
        SpfTerm term, int depth, List<string> path,
        RecordExpansion expansion, WalkState state, bool bypassCache, CancellationToken ct)
    {
        var target = term.Target!;
        if (term.HasMacro)
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved,
                "Macro in the target — unexpandable without a sender identity, kept.", []));
            return;
        }

        if (IsIpLiteral(target))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Target is an IP address, not a domain — kept as-is.", []));
            return;
        }

        var normalized = Normalize(target);
        if (StubProviders.Contains(normalized))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved,
                "Volatile provider set — deliberately not expanded, kept as a stub include.", []));
            return;
        }

        if (path.Contains(normalized))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Include cycle — kept as-is to preserve current behavior.", []));
            return;
        }

        if (depth + 1 > MaxDepth)
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Deeper than 10 includes — kept as-is.", []));
            return;
        }

        var records = await FetchSpfRecordsAsync(normalized, state, bypassCache, ct);
        if (records is null)
        {
            expansion.Failures.Add($"{target}: DNS lookup failed during expansion.");
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Could not be resolved — kept as-is, unverified.", []));
            return;
        }

        if (records.Count != 1)
        {
            var reason = records.Count == 0
                ? "Publishes no SPF record — kept as-is (dropping would bake in today's DNS)."
                : "Publishes several SPF records (permerror) — kept as-is.";
            expansion.Failures.Add($"{target}: {reason}");
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, reason, []));
            return;
        }

        path.Add(normalized);
        try
        {
            // Nested all/modifiers do not splice — only addresses and preserved
            // terms, in place. Anything unknown deeper down is already preserved
            // verbatim there, so splicing always stays sound; nothing fails upward.
            var nested = await ExpandRecordAsync(normalized, SpfRecordParser.Parse(records[0]),
                depth + 1, path, state, isEffectiveRoot: false, bypassCache, ct);
            expansion.Absorb(nested);
            var note = nested.DroppedUnmatchable == 0
                ? null
                : $"{nested.DroppedUnmatchable} nested term(s) cannot match through an include — dropped.";
            expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Expanded, note, nested.OrderedTerms));
        }
        finally
        {
            path.Remove(normalized);
        }
    }

    private async Task ExpandRedirectAsync(
        SpfTerm term, int depth, List<string> path,
        RecordExpansion expansion, WalkState state, bool isEffectiveRoot, bool bypassCache,
        CancellationToken ct)
    {
        var target = term.Target!;
        if (term.HasMacro || string.IsNullOrWhiteSpace(target) || IsIpLiteral(target))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Unfollowable redirect target — kept as-is.", []));
            return;
        }

        var normalized = Normalize(target);
        if (path.Contains(normalized) || depth + 1 > MaxDepth)
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Redirect cycle or too deep — kept as-is.", []));
            return;
        }

        var records = await FetchSpfRecordsAsync(normalized, state, bypassCache, ct);
        if (records is null || records.Count != 1)
        {
            var reason = records is null
                ? "Redirect target could not be resolved — kept as-is, unverified."
                : records.Count == 0
                    ? "Redirect target publishes no SPF record — kept as-is."
                    : "Redirect target publishes several SPF records (permerror) — kept as-is.";
            expansion.Failures.Add($"{target}: {reason}");
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, reason, []));
            return;
        }

        path.Add(normalized);
        try
        {
            // At the top level the redirect target IS the effective record: its
            // all and modifiers win. Nested, it is just another branch.
            var nested = await ExpandRecordAsync(normalized, SpfRecordParser.Parse(records[0]),
                depth + 1, path, state, isEffectiveRoot, bypassCache, ct);
            if (isEffectiveRoot)
            {
                expansion.AbsorbRedirect(nested);
            }
            else
            {
                expansion.Absorb(nested);
            }

            expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Expanded, $"Delegates to {normalized}.", nested.OrderedTerms));
        }
        finally
        {
            path.Remove(normalized);
        }
    }

    private async Task ExpandMxAsync(
        string domain, SpfTerm term, RecordExpansion expansion, WalkState state, bool bypassCache,
        CancellationToken ct)
    {
        if (term.HasMacro)
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved,
                "Macro in the target — unexpandable without a sender identity, kept.", []));
            return;
        }

        if (term.Target is not null && IsIpLiteral(term.Target))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Target is an IP address, not a domain — kept as-is.", []));
            return;
        }

        if (OutOfBudget(state))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "MX hosts not resolved — the analysis budget ran out first.", []));
            return;
        }

        var target = term.Target is null ? domain : Normalize(term.Target);
        var hosts = await mx.ResolveAsync(target, ct, bypassCache);
        state.Queries++;
        if (hosts is null)
        {
            expansion.Failures.Add($"{target}: MX lookup failed during expansion.");
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "MX lookup failed — kept as-is, unverified.", []));
            return;
        }

        if (hosts.Count == 0)
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved,
                "No MX records today — kept (DNS may change; dropping would bake in today).", []));
            return;
        }

        if (hosts.Count > MaxMxExchanges)
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved,
                $"{hosts.Count} MX exchanges — beyond the {MaxMxExchanges}-host expansion cap, kept whole.", []));
            return;
        }

        // All-or-nothing: one unresolvable exchange preserves the whole term.
        // Exchanges visit in name order so the output does not depend on DNS order.
        var expanded = new List<string>();
        foreach (var host in hosts.OrderBy(h => h.Host, StringComparer.Ordinal))
        {
            var ips = await ResolveHostAddressesAsync(host.Host, term, state, bypassCache, ct);
            if (ips is null)
            {
                expansion.Failures.Add($"{host.Host}: address lookup failed during expansion.");
                expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                    SpfCandidateTermOutcome.Preserved,
                    $"Exchange {host.Host} could not be resolved — kept whole, unverified.", []));
                return;
            }

            expanded.AddRange(ips);
        }

        expansion.OrderedTerms.AddRange(expanded);
        expansion.ExpandedIpCount += expanded.Count;
        expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
            SpfCandidateTermOutcome.Expanded, null, expanded));
    }

    private async Task ExpandHostAsync(
        string domain, SpfTerm term, RecordExpansion expansion, WalkState state, bool bypassCache,
        CancellationToken ct)
    {
        if (term.HasMacro)
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved,
                "Macro in the target — unexpandable without a sender identity, kept.", []));
            return;
        }

        if (term.Target is not null && IsIpLiteral(term.Target))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Target is an IP address, not a domain — kept as-is.", []));
            return;
        }

        if (OutOfBudget(state))
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Addresses not resolved — the analysis budget ran out first.", []));
            return;
        }

        var name = term.Target is null ? domain : Normalize(term.Target);
        var ips = await ResolveHostAddressesAsync(name, term, state, bypassCache, ct);
        if (ips is null)
        {
            expansion.Failures.Add($"{name}: address lookup failed during expansion.");
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved, "Address lookup failed — kept as-is, unverified.", []));
            return;
        }

        if (ips.Count == 0)
        {
            expansion.AddPreserved(term.Text, new SpfCandidateTermDto(term.Text,
                SpfCandidateTermOutcome.Preserved,
                $"No addresses at {name} today — kept (DNS may change; dropping would bake in today).", []));
            return;
        }

        expansion.OrderedTerms.AddRange(ips);
        expansion.ExpandedIpCount += ips.Count;
        expansion.TermOutcomes.Add(new SpfCandidateTermDto(term.Text,
            SpfCandidateTermOutcome.Expanded, null, ips));
    }

    /// <summary>
    /// Resolve A/AAAA for one host and render ip4:/ip6: terms with the mechanism's
    /// qualifier and CIDR applied per address. Sorted numerically so DNS answer
    /// order never leaks into the candidate. Null when the lookup itself failed.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ResolveHostAddressesAsync(
        string host, SpfTerm term, WalkState state, bool bypassCache, CancellationToken ct)
    {
        var resolved = await addresses.ResolveAsync(host, ct, bypassCache);
        state.Queries += 2; // one A query and one AAAA query
        if (resolved is null)
        {
            return null;
        }

        var prefix = term.Qualifier == "+" ? string.Empty : term.Qualifier;
        var terms = new List<string>();
        foreach (var ip in resolved.V4.OrderBy(IpKey, IpBytesComparer))
        {
            terms.Add(term.Cidr4 is null ? $"{prefix}ip4:{ip}" : $"{prefix}ip4:{ip}/{term.Cidr4}");
        }

        foreach (var ip in resolved.V6.OrderBy(IpKey, IpBytesComparer))
        {
            terms.Add(term.Cidr6 is null ? $"{prefix}ip6:{ip}" : $"{prefix}ip6:{ip}/{term.Cidr6}");
        }

        return terms;
    }

    private async Task<IReadOnlyList<string>?> FetchSpfRecordsAsync(
        string domain, WalkState state, bool bypassCache, CancellationToken ct)
    {
        var txts = await txt.ResolveAsync(domain, ct, bypassCache);
        state.Queries++;
        return txts?.Where(t => t.TrimStart().StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string? FollowableRedirect(SpfRecord record)
    {
        var target = record.RedirectTarget;
        if (string.IsNullOrWhiteSpace(target) || target.Contains("%{", StringComparison.Ordinal) || IsIpLiteral(target))
        {
            return null;
        }

        return target;
    }

    private static bool OutOfBudget(WalkState state)
        => state.Queries >= MaxQueries || DateTime.UtcNow - state.StartedUtc > ElapsedLimit;

    /// <summary>
    /// Assemble the candidate in original term order (evaluation stops at the
    /// first match, so order is semantics): expansions spliced in place, exact
    /// duplicates dropped keeping the first, the effective all qualifier last.
    /// </summary>
    private static string Assemble(RecordExpansion expansion)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string> { "v=spf1" };
        foreach (var term in expansion.OrderedTerms)
        {
            if (seen.Add(term))
            {
                parts.Add(term);
            }
        }

        parts.Add($"{expansion.AllQualifier ?? "?"}all");
        return string.Join(' ', parts);
    }

    private static int Segments(int length)
        => (length + 254) / 255; // TXT character-strings carry 255 bytes each

    private static string Normalize(string domain)
        => domain.Trim().TrimEnd('.').ToLowerInvariant();

    private static string NormalizeRecord(string raw)
        => string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsIpLiteral(string value)
        => IPAddress.TryParse(value.Trim().TrimEnd('.'), out _);

    private static byte[] IpKey(IPAddress ip)
        => ip.GetAddressBytes();

    private static readonly IComparer<byte[]> IpBytesComparer =
        Comparer<byte[]>.Create((a, b) =>
        {
            // v4 (4 bytes) sorts before v6 (16 bytes); within a family, numeric.
            var length = a.Length.CompareTo(b.Length);
            if (length != 0)
            {
                return length;
            }

            for (var i = 0; i < a.Length; i++)
            {
                var cmp = a[i].CompareTo(b[i]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return 0;
        });

    private sealed class WalkState
    {
        public DateTime StartedUtc;
        public int Queries;
    }

    private sealed class RecordExpansion
    {
        /// <summary>Candidate terms in original order — the only list Assemble reads.</summary>
        public List<string> OrderedTerms { get; } = [];

        /// <summary>Per-term outcomes for the original→candidate diff (top level only).</summary>
        public List<SpfCandidateTermDto> TermOutcomes { get; } = [];

        public List<string> Failures { get; } = [];
        public string? AllQualifier;
        public int ExpandedIpCount;
        public int DroppedUnmatchable;

        public void AddPreserved(string text, SpfCandidateTermDto outcome)
        {
            OrderedTerms.Add(text);
            TermOutcomes.Add(outcome);
        }

        /// <summary>Splice a nested expansion in place: its terms land where the include stood.</summary>
        public void Absorb(RecordExpansion nested)
        {
            OrderedTerms.AddRange(nested.OrderedTerms);
            Failures.AddRange(nested.Failures);
            ExpandedIpCount += nested.ExpandedIpCount;
            DroppedUnmatchable += nested.DroppedUnmatchable;
        }

        /// <summary>
        /// A followed top-level redirect replaces the record: the target's terms,
        /// all and failure notes win; the (empty) local list is already all drops.
        /// </summary>
        public void AbsorbRedirect(RecordExpansion nested)
        {
            OrderedTerms.AddRange(nested.OrderedTerms);
            Failures.AddRange(nested.Failures);
            ExpandedIpCount += nested.ExpandedIpCount;
            AllQualifier = nested.AllQualifier;
        }
    }
}
