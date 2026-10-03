using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

/// <summary>Outcome of the drift check's root SPF lookup.</summary>
public static class SpfDriftRecordStatus
{
    public const string Found = "found";
    public const string Missing = "missing";
    public const string LookupFailed = "lookup_failed";

    /// <summary>Two or more v=spf1 records — receivers permerror, so there is no usable record to snapshot.</summary>
    public const string Invalid = "invalid";
}

/// <summary>One resolved dependency target: the record it published at check time, null when unknown.</summary>
public sealed record SpfDependencySnapshotEntry(string Domain, string? Record, string? Hash);

/// <summary>
/// One drift check: the root record's fate, the dependency snapshot, and the
/// candidate that snapshot yields. Pure data — folding it into the row is the
/// state cache's job.
/// </summary>
public sealed record SpfDriftCheckResult(
    string SpfRecordStatus,
    string? RawRecord,
    IReadOnlyList<SpfDependencySnapshotEntry> Dependencies,
    string? DependencyHash,
    SpfCandidateDto? Candidate,
    string? CandidateHash,
    int PublishedLookups,
    bool PublishedOverBudget,
    IReadOnlyList<string> Issues);

public interface ISpfDriftCheckService
{
    /// <summary>
    /// Check one domain: walk its SPF tree (#42), generate its candidate
    /// (#43), and snapshot every dependency target's published record.
    /// Never throws except on cancellation — both halves already degrade
    /// internally, and anything unexpected reads as a lookup failure.
    /// </summary>
    /// <param name="bypassCache">See <see cref="IDnsTxtResolver.ResolveAsync"/> — same rationale.</param>
    Task<SpfDriftCheckResult> CheckAsync(string domainName, CancellationToken ct, bool bypassCache = false);
}

/// <summary>
/// The read half of drift monitoring: analysis, candidate, and snapshot in one
/// pass over the shared resolvers, so the three walks cost little more than
/// one. The write half (change detection, last-known-good) lives in
/// <see cref="SpfDriftStateCache"/>.
/// </summary>
public sealed class SpfDriftCheckService(
    ISpfDependencyAnalyzer analyzer,
    ISpfCandidateGenerator generator) : ISpfDriftCheckService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<SpfDriftCheckResult> CheckAsync(string domainName, CancellationToken ct, bool bypassCache = false)
    {
        try
        {
            var analysis = await analyzer.AnalyzeAsync(domainName, ct, bypassCache);
            var status = MapRootStatus(analysis.Root.Status);
            if (status != SpfDriftRecordStatus.Found)
            {
                return new SpfDriftCheckResult(status, null, [], null, null, null,
                    analysis.TotalLookups, analysis.OverBudget, analysis.Issues);
            }

            var raw = analysis.Root.Raw!;
            var candidate = await generator.GenerateAsync(domainName, ct, bypassCache);
            var dependencies = CollectDependencies(analysis.Root);
            return new SpfDriftCheckResult(
                SpfDriftRecordStatus.Found, raw, dependencies,
                HashSnapshot(raw, dependencies),
                candidate,
                candidate.Status == SpfCandidateStatus.Ready && candidate.Candidate is not null
                    ? HashText(candidate.Candidate)
                    : null,
                analysis.TotalLookups, analysis.OverBudget, analysis.Issues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Both halves promise never to throw; this is the belt next to
            // their suspenders — a check that crashes reads as a failed
            // lookup, keeping last-known-good, never as drift.
            return new SpfDriftCheckResult(SpfDriftRecordStatus.LookupFailed, null, [], null, null, null,
                0, false, [$"SPF drift check failed unexpectedly ({ex.GetType().Name})."]);
        }
    }

    /// <summary>
    /// Every followed dependency target with the record it published — the
    /// reviewable snapshot a drift diff is computed from. First visit wins per
    /// domain (a target can appear on several branches); sorted for determinism.
    /// </summary>
    public static IReadOnlyList<SpfDependencySnapshotEntry> CollectDependencies(SpfDependencyNodeDto root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<SpfDependencySnapshotEntry>();
        Collect(root, seen, entries);
        entries.Sort((a, b) => string.Compare(a.Domain, b.Domain, StringComparison.Ordinal));
        return entries;
    }

    private static void Collect(
        SpfDependencyNodeDto node, HashSet<string> seen, List<SpfDependencySnapshotEntry> entries)
    {
        foreach (var term in node.Terms)
        {
            var child = term.Resolution;
            if (child is null || !seen.Add(child.Domain))
            {
                continue;
            }

            entries.Add(new SpfDependencySnapshotEntry(
                child.Domain, child.Raw, child.Raw is null ? null : HashText(child.Raw)));
            Collect(child, seen, entries);
        }
    }

    /// <summary>
    /// The change-detection hash: the root record plus every snapshot entry,
    /// normalized (whitespace-collapsed, lowercased) so semantically identical
    /// records hash the same. Display keeps the original text; only the hash
    /// normalizes.
    /// </summary>
    public static string HashSnapshot(string rawRecord, IReadOnlyList<SpfDependencySnapshotEntry> dependencies)
    {
        var text = new StringBuilder(Normalize(rawRecord));
        foreach (var entry in dependencies.OrderBy(e => e.Domain, StringComparer.Ordinal))
        {
            text.Append('\n').Append(entry.Domain.ToLowerInvariant()).Append('\t')
                .Append(entry.Record is null ? "∅" : Normalize(entry.Record));
        }

        return HashText(text.ToString());
    }

    public static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string SerializeDependencies(IReadOnlyList<SpfDependencySnapshotEntry> dependencies)
        => JsonSerializer.Serialize(dependencies, Json);

    public static IReadOnlyList<SpfDependencySnapshotEntry> DeserializeDependencies(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SpfDependencySnapshotEntry>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string MapRootStatus(string nodeStatus) => nodeStatus switch
    {
        SpfNodeStatus.Found => SpfDriftRecordStatus.Found,
        SpfNodeStatus.Missing => SpfDriftRecordStatus.Missing,
        SpfNodeStatus.Permerror => SpfDriftRecordStatus.Invalid,
        _ => SpfDriftRecordStatus.LookupFailed,
    };

    private static string Normalize(string raw)
        => string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
}
