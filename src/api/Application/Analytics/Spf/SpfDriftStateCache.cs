using System.Text.Json;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

public sealed record SpfDriftRefreshResult(int Checked, int Changed, int Failed);

public interface ISpfDriftStateCache
{
    /// <summary>Check every active domain, fold results into the rows, and report the pass.</summary>
    Task<SpfDriftRefreshResult> RefreshAllAsync(CancellationToken ct);

    /// <summary>Stores one check result (the on-demand recheck path) and saves.</summary>
    Task<SpfDriftState> ApplyAsync(Guid domainId, SpfDriftCheckResult result, CancellationToken ct);
}

/// <summary>
/// Persists SPF drift check results on <c>spf_drift_state</c>, one row per domain.
/// <para>
/// Same doctrine as <see cref="MtaSts.MtaStsStateCache"/>: a failed lookup keeps
/// the last known values rather than blanking them, nothing here bumps the
/// domain's UpdatedAtUtc, and no audit events are written — a background check
/// is not an operator action. Change *notification* is the alert evaluator's
/// job, reading the columns this writes.
/// </para>
/// </summary>
public sealed class SpfDriftStateCache(
    DmarcAnalyzerDbContext db,
    ISpfDriftCheckService checkService,
    IOptions<SpfDriftOptions> options,
    ILogger<SpfDriftStateCache> logger) : ISpfDriftStateCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SpfDriftRefreshResult> RefreshAllAsync(CancellationToken ct)
    {
        var domains = await db.Domains
            .Where(x => x.IsActive)
            .Select(x => new
            {
                x.Id,
                x.Name,
                LastCheckedAtUtc = db.SpfDriftStates
                    .Where(s => s.DomainId == x.Id)
                    .Select(s => (DateTime?)s.LastCheckedAtUtc)
                    .FirstOrDefault(),
            })
            .OrderBy(x => x.LastCheckedAtUtc == null ? 0 : 1)
            .ThenBy(x => x.LastCheckedAtUtc)
            .ToListAsync(ct);

        if (domains.Count == 0)
        {
            return new SpfDriftRefreshResult(0, 0, 0);
        }

        // Network phase, concurrent and DbContext-free. A crashed or timed-out
        // check maps to a lookup-failed result so one bad domain cannot take
        // down the pass; each check starts on a random delay so a large fleet
        // does not burst the resolver the moment the pass begins.
        var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentChecks));
        var checks = await Task.WhenAll(domains.Select(async domain =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var jitterMs = options.Value.StartJitterSeconds <= 0
                    ? 0
                    : Random.Shared.Next(0, options.Value.StartJitterSeconds * 1000 + 1);
                if (jitterMs > 0)
                {
                    await Task.Delay(jitterMs, ct);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(
                    Math.Max(5, options.Value.PerDomainTimeoutSeconds)));
                try
                {
                    return (domain.Id, Result: await checkService.CheckAsync(domain.Name, timeout.Token));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "SPF drift check timed out for {Domain} after {Seconds}s",
                        domain.Name, options.Value.PerDomainTimeoutSeconds);
                    return (domain.Id, Result: TimedOutCheck(options.Value.PerDomainTimeoutSeconds));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SPF drift check crashed for {Domain}", domain.Name);
                return (domain.Id, Result: CrashedCheck());
            }
            finally
            {
                gate.Release();
            }
        }));

        // Write phase, sequential on the tracked context.
        var domainIds = domains.Select(d => d.Id).ToList();
        var states = await db.SpfDriftStates
            .Where(s => domainIds.Contains(s.DomainId))
            .ToDictionaryAsync(s => s.DomainId, ct);

        var changed = 0;
        var failed = 0;
        var now = DateTime.UtcNow;

        foreach (var (domainId, result) in checks)
        {
            if (!states.TryGetValue(domainId, out var state))
            {
                state = new SpfDriftState { DomainId = domainId };
                db.SpfDriftStates.Add(state);
            }

            if (Apply(state, result, now))
            {
                changed++;
            }

            if (result.SpfRecordStatus == SpfDriftRecordStatus.LookupFailed)
            {
                failed++;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "SPF drift refresh: checked {Checked}, changed {Changed}, lookup failures {Failed}",
            domains.Count, changed, failed);

        return new SpfDriftRefreshResult(domains.Count, changed, failed);
    }

    public async Task<SpfDriftState> ApplyAsync(Guid domainId, SpfDriftCheckResult result, CancellationToken ct)
    {
        var state = await db.SpfDriftStates.SingleOrDefaultAsync(s => s.DomainId == domainId, ct);
        if (state is null)
        {
            state = new SpfDriftState { DomainId = domainId };
            db.SpfDriftStates.Add(state);
        }

        Apply(state, result, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return state;
    }

    /// <summary>
    /// Folds one check result into the row. Public static so the keep-last-known
    /// rules are unit-testable without DNS or a database.
    /// <para>
    /// The rules, per root outcome:
    /// lookup_failed keeps everything (a SERVFAIL must not retire a candidate
    /// or read as "no SPF"); missing is definitive and clears the record
    /// fields including the change history, because a withdrawn record is not
    /// drift; invalid keeps the last-known snapshot and candidate (nothing
    /// further was checkable) but records the breakage as the status; found
    /// stores fresh values, moving the current hash/status aside as previous
    /// when they moved — except the first observation, which is not a change.
    /// </para>
    /// </summary>
    public static bool Apply(SpfDriftState state, SpfDriftCheckResult check, DateTime nowUtc)
    {
        var before = MaterialSnapshot(state);

        switch (check.SpfRecordStatus)
        {
            case SpfDriftRecordStatus.LookupFailed:
                state.SpfRecordStatus = SpfDriftRecordStatus.LookupFailed;
                state.ConsecutiveFailures++;
                break;

            case SpfDriftRecordStatus.Missing:
            case SpfDriftRecordStatus.Invalid:
                state.SpfRecordStatus = check.SpfRecordStatus;
                if (check.SpfRecordStatus == SpfDriftRecordStatus.Missing)
                {
                    ClearRecordFields(state);
                }

                state.ConsecutiveFailures = 0;
                state.LastSuccessAtUtc = nowUtc;
                break;

            case SpfDriftRecordStatus.Found:
                state.SpfRecordStatus = SpfDriftRecordStatus.Found;
                state.RawRecord = Truncate(check.RawRecord, 4096);

                if (state.DependencyHash is not null
                    && check.DependencyHash is not null
                    && !string.Equals(state.DependencyHash, check.DependencyHash, StringComparison.Ordinal))
                {
                    state.PreviousDependencyHash = state.DependencyHash;
                    state.PreviousDependencySnapshotJson = state.DependencySnapshotJson;
                    state.PreviousPublishedLookups = state.PublishedLookups;
                    state.PreviousCandidateLookups = state.CandidateLookups;
                    state.PreviousCandidateLength = state.CandidateLength;
                    state.PreviousPublishedOverBudget = state.PublishedOverBudget;
                    state.PreviousCandidateText = state.CandidateText;
                    state.PreviousCandidateHash = state.CandidateHash;
                    state.DependencyChangedAtUtc = nowUtc;
                }

                state.DependencyHash = check.DependencyHash;
                state.DependencySnapshotJson = SpfDriftCheckService.SerializeDependencies(check.Dependencies);

                if (state.CandidateStatus is not null
                    && check.Candidate?.Status is not null
                    && !string.Equals(state.CandidateStatus, check.Candidate.Status, StringComparison.Ordinal))
                {
                    state.PreviousCandidateStatus = state.CandidateStatus;
                    state.CandidateChangedAtUtc = nowUtc;
                }

                state.CandidateStatus = check.Candidate?.Status;
                state.CandidateText = Truncate(check.Candidate?.Candidate, 4096);
                state.CandidateHash = check.CandidateHash;
                state.PublishedLookups = check.PublishedLookups;
                state.CandidateLookups = check.Candidate?.CandidateLookups;
                state.CandidateLength = check.Candidate?.CandidateLength;
                state.PublishedOverBudget = check.PublishedOverBudget;
                state.ConsecutiveFailures = 0;
                state.LastSuccessAtUtc = nowUtc;
                break;
        }

        state.IssuesJson = check.Issues.Count == 0 ? null : JsonSerializer.Serialize(check.Issues, Json);

        // Always advanced, even when nothing moved: "we verified this" is what it is for.
        state.LastCheckedAtUtc = nowUtc;

        var changed = MaterialSnapshot(state) != before;
        if (changed)
        {
            state.LastChangedAtUtc = nowUtc;
        }

        return changed;
    }

    private static void ClearRecordFields(SpfDriftState state)
    {
        state.RawRecord = null;
        state.DependencySnapshotJson = null;
        state.DependencyHash = null;
        state.PreviousDependencyHash = null;
        state.PreviousDependencySnapshotJson = null;
        state.DependencyChangedAtUtc = null;
        state.CandidateStatus = null;
        state.CandidateText = null;
        state.CandidateHash = null;
        state.PreviousCandidateStatus = null;
        state.CandidateChangedAtUtc = null;
        state.PublishedLookups = null;
        state.CandidateLookups = null;
        state.CandidateLength = null;
        state.PublishedOverBudget = null;
        state.PreviousPublishedLookups = null;
        state.PreviousCandidateLookups = null;
        state.PreviousCandidateLength = null;
        state.PreviousPublishedOverBudget = null;
        state.PreviousCandidateText = null;
        state.PreviousCandidateHash = null;
    }

    /// <summary>
    /// The fields whose movement counts as "something changed" — everything
    /// except the always-advancing timestamps and the failure counter, so a
    /// run of SERVFAILs is not "change".
    /// </summary>
    private static string MaterialSnapshot(SpfDriftState s) => string.Join('\u001f',
        s.SpfRecordStatus, s.RawRecord, s.DependencySnapshotJson, s.DependencyHash,
        s.PreviousDependencyHash, s.PreviousDependencySnapshotJson,
        s.CandidateStatus, s.CandidateText, s.CandidateHash,
        s.PreviousCandidateStatus, s.PublishedLookups?.ToString(), s.CandidateLookups?.ToString(),
        s.CandidateLength?.ToString(), s.PublishedOverBudget?.ToString(),
        s.PreviousPublishedLookups?.ToString(), s.PreviousCandidateLookups?.ToString(),
        s.PreviousCandidateLength?.ToString(), s.PreviousPublishedOverBudget?.ToString(),
        s.PreviousCandidateText, s.PreviousCandidateHash, s.IssuesJson);

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    private static SpfDriftCheckResult TimedOutCheck(int seconds)
    {
        var issues = new[] { $"The check exceeded its {seconds}s per-domain budget — DNS too slow to trust the result." };
        return new SpfDriftCheckResult(
            SpfDriftRecordStatus.LookupFailed, null, [], null, null, null, 0, false, issues);
    }

    private static SpfDriftCheckResult CrashedCheck()
    {
        var issues = new[] { "The check failed unexpectedly — see the worker log." };
        return new SpfDriftCheckResult(
            SpfDriftRecordStatus.LookupFailed, null, [], null, null, null, 0, false, issues);
    }
}
