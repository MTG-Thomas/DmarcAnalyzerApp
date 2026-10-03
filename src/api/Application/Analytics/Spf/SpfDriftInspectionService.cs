using System.Text.Json;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

/// <summary>The SPF drift panel's read/recheck API — see <see cref="SpfDriftInspectionService"/>.</summary>
public interface ISpfDriftInspectionService
{
    /// <summary>
    /// The persisted SPF drift state for a domain — database only, no network,
    /// so the panel renders instantly. Null for unknown or cross-tenant ids.
    /// </summary>
    Task<SpfDriftStateDto?> GetAsync(Guid domainId, CancellationToken ct);

    /// <summary>
    /// Runs a live drift check now, persists it (keep-last-known rules apply)
    /// and returns the updated state. Null for unknown or cross-tenant ids.
    /// </summary>
    Task<SpfDriftStateDto?> RecheckAsync(Guid domainId, CancellationToken ct);
}

/// <summary>Tenancy-checked orchestration over the drift check service and state cache.</summary>
public sealed class SpfDriftInspectionService(
    DmarcAnalyzerDbContext db,
    ICurrentUserContext currentUser,
    ISpfDriftCheckService checkService,
    ISpfDriftStateCache stateCache) : ISpfDriftInspectionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<SpfDriftStateDto?> GetAsync(Guid domainId, CancellationToken ct)
    {
        var domain = await ResolveAccessibleDomainAsync(domainId, ct);
        if (domain is null)
        {
            return null;
        }

        var state = await db.SpfDriftStates
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.DomainId == domainId, ct);

        return ToDto(domain.Value.Id, domain.Value.Name, state);
    }

    /// <inheritdoc />
    public async Task<SpfDriftStateDto?> RecheckAsync(Guid domainId, CancellationToken ct)
    {
        var domain = await ResolveAccessibleDomainAsync(domainId, ct);
        if (domain is null)
        {
            return null;
        }

        // bypassCache: an operator clicking "Recheck now" right after a provider
        // change must see it — the resolvers' 5-minute cache would otherwise keep
        // serving the pre-change answer for the rest of the TTL.
        var result = await checkService.CheckAsync(domain.Value.Name, ct, bypassCache: true);
        var state = await stateCache.ApplyAsync(domain.Value.Id, result, ct);
        return ToDto(domain.Value.Id, domain.Value.Name, state);
    }

    private async Task<(Guid Id, string Name)?> ResolveAccessibleDomainAsync(Guid domainId, CancellationToken ct)
    {
        var domain = await db.Domains
            .AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => new { x.Id, x.Name, x.ClientId })
            .SingleOrDefaultAsync(ct);

        // Cross-tenant ids read as not-found to avoid an existence oracle.
        if (domain is null || !currentUser.CanAccessClient(domain.ClientId))
        {
            return null;
        }

        return (domain.Id, domain.Name);
    }

    private static SpfDriftStateDto ToDto(Guid domainId, string name, SpfDriftState? state)
    {
        if (state is null)
        {
            return new SpfDriftStateDto(
                domainId, name, Checked: false,
                null, null, [], [], null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, [], null, null, null, null);
        }

        return new SpfDriftStateDto(
            domainId, name, Checked: true,
            state.SpfRecordStatus,
            state.RawRecord,
            SpfDriftCheckService.DeserializeDependencies(state.DependencySnapshotJson),
            SpfDriftCheckService.DeserializeDependencies(state.PreviousDependencySnapshotJson),
            state.DependencyChangedAtUtc,
            state.CandidateStatus,
            state.CandidateText,
            state.PreviousCandidateStatus,
            state.PreviousCandidateText,
            state.CandidateChangedAtUtc,
            state.PublishedLookups,
            state.CandidateLookups,
            state.CandidateLength,
            state.PublishedOverBudget,
            state.PreviousPublishedLookups,
            state.PreviousCandidateLookups,
            state.PreviousCandidateLength,
            state.PreviousPublishedOverBudget,
            DeserializeIssues(state.IssuesJson),
            state.LastCheckedAtUtc,
            state.LastChangedAtUtc,
            state.LastSuccessAtUtc,
            state.ConsecutiveFailures);
    }

    private static IReadOnlyList<string> DeserializeIssues(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return []; // a malformed stored blob renders as empty, not as a 500
        }
    }
}
