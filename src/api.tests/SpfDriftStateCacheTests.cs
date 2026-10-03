using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Analytics.Spf;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The keep-last-known rules: a transient failure must not retire a candidate
/// or read as "no SPF", while a definitive absence clears everything. Guarded
/// here because the worker pass exercises them against real DNS, where the
/// failure cases are exactly the ones that never occur in a demo.
/// </summary>
public sealed class SpfDriftStateCacheTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SpfDriftCheckResult Found(
        string raw = "v=spf1 include:mid.example.com -all",
        string targetRecord = "v=spf1 ip4:198.51.100.0/24 -all",
        string candidateStatus = SpfCandidateStatus.Ready,
        string? candidate = "v=spf1 ip4:198.51.100.0/24 -all",
        int lookups = 1,
        bool overBudget = false,
        string[]? issues = null)
    {
        var deps = (IReadOnlyList<SpfDependencySnapshotEntry>)
            [new("mid.example.com", targetRecord, SpfDriftCheckService.HashText(targetRecord))];
        var dto = new SpfCandidateDto(candidateStatus, raw, candidateStatus == SpfCandidateStatus.Ready ? candidate : null,
            [], [], lookups, candidateStatus == SpfCandidateStatus.Ready ? 0 : 1,
            candidate?.Length ?? 0, 1);
        return new SpfDriftCheckResult(SpfDriftRecordStatus.Found, raw, deps,
            SpfDriftCheckService.HashSnapshot(raw, deps), dto,
            dto.Candidate is null ? null : SpfDriftCheckService.HashText(dto.Candidate),
            lookups, overBudget, issues ?? []);
    }

    private static SpfDriftCheckResult LookupFailed()
        => new(SpfDriftRecordStatus.LookupFailed, null, [], null, null, null, 0, false,
            ["DNS lookup failed — could not check the record."]);

    private static SpfDriftCheckResult Missing()
        => new(SpfDriftRecordStatus.Missing, null, [], null, null, null, 0, false, []);

    private static SpfDriftCheckResult Invalid()
        => new(SpfDriftRecordStatus.Invalid, null, [], null, null, null, 0, false,
            ["Two SPF records published — receivers reject all of them."]);

    [Fact]
    public void FirstFoundCheck_StoresEverything_WithoutChangeMarkers()
    {
        var state = new SpfDriftState { DomainId = Guid.NewGuid() };

        var changed = SpfDriftStateCache.Apply(state, Found(), T0);

        Assert.True(changed);
        Assert.Equal(SpfDriftRecordStatus.Found, state.SpfRecordStatus);
        Assert.Equal("v=spf1 include:mid.example.com -all", state.RawRecord);
        Assert.NotNull(state.DependencyHash);
        Assert.Contains("mid.example.com", state.DependencySnapshotJson);
        Assert.Null(state.PreviousDependencyHash); // first observation is not a change
        Assert.Null(state.DependencyChangedAtUtc);
        Assert.Equal(SpfCandidateStatus.Ready, state.CandidateStatus);
        Assert.Null(state.PreviousCandidateStatus);
        Assert.Equal(1, state.PublishedLookups);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(T0, state.LastSuccessAtUtc);
        Assert.Equal(T0, state.LastCheckedAtUtc);
    }

    [Fact]
    public void LookupFailed_KeepsLastKnownGood()
    {
        var state = new SpfDriftState { DomainId = Guid.NewGuid() };
        SpfDriftStateCache.Apply(state, Found(), T0);

        var changed = SpfDriftStateCache.Apply(state, LookupFailed(), T0.AddHours(12));

        Assert.True(changed); // the status flip itself is material; only repeats are not
        Assert.Equal(SpfDriftRecordStatus.LookupFailed, state.SpfRecordStatus);
        Assert.Equal("v=spf1 include:mid.example.com -all", state.RawRecord);
        Assert.NotNull(state.DependencyHash);
        Assert.Equal(SpfCandidateStatus.Ready, state.CandidateStatus);
        Assert.NotNull(state.CandidateText);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(T0, state.LastSuccessAtUtc); // untouched — freshness now unknown
        Assert.Equal(T0.AddHours(12), state.LastCheckedAtUtc);
    }

    [Fact]
    public void RepeatedFailures_AreNotMaterialChange()
    {
        var state = new SpfDriftState { DomainId = Guid.NewGuid() };
        SpfDriftStateCache.Apply(state, Found(), T0);
        SpfDriftStateCache.Apply(state, LookupFailed(), T0.AddHours(12));

        var changed = SpfDriftStateCache.Apply(state, LookupFailed(), T0.AddHours(24));

        Assert.False(changed);
        Assert.Equal(2, state.ConsecutiveFailures);
        // The flip to lookup_failed at T0+12 was material, so the stamp stays there.
        Assert.Equal(T0.AddHours(12), state.LastChangedAtUtc);
    }

    [Fact]
    public void Missing_ClearsRecordFieldsAndHistory()
    {
        var state = new SpfDriftState { DomainId = Guid.NewGuid() };
        SpfDriftStateCache.Apply(state, Found(), T0);
        SpfDriftStateCache.Apply(state, Found(targetRecord: "v=spf1 ip4:192.0.2.1 -all"), T0.AddHours(6));

        SpfDriftStateCache.Apply(state, Missing(), T0.AddHours(12));

        Assert.Equal(SpfDriftRecordStatus.Missing, state.SpfRecordStatus);
        Assert.Null(state.RawRecord);
        Assert.Null(state.DependencyHash);
        Assert.Null(state.PreviousDependencyHash);
        Assert.Null(state.DependencyChangedAtUtc);
        Assert.Null(state.CandidateStatus);
        Assert.Null(state.CandidateText);
        Assert.Null(state.PublishedLookups);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(T0.AddHours(12), state.LastSuccessAtUtc); // absence is conclusive
    }

    [Fact]
    public void Invalid_KeepsSnapshotAndCandidate_ButMarksTheBreakage()
    {
        var state = new SpfDriftState { DomainId = Guid.NewGuid() };
        SpfDriftStateCache.Apply(state, Found(), T0);

        var changed = SpfDriftStateCache.Apply(state, Invalid(), T0.AddHours(6));

        Assert.True(changed);
        Assert.Equal(SpfDriftRecordStatus.Invalid, state.SpfRecordStatus);
        Assert.NotNull(state.DependencyHash); // kept — nothing further was checkable
        Assert.NotNull(state.CandidateText); // kept
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(T0.AddHours(6), state.LastSuccessAtUtc);
    }

    [Fact]
    public void DependencyMove_SetsPreviousValuesAndTimestamp()
    {
        var state = new SpfDriftState { DomainId = Guid.NewGuid() };
        var first = Found();
        SpfDriftStateCache.Apply(state, first, T0);

        var changed = SpfDriftStateCache.Apply(
            state, Found(targetRecord: "v=spf1 ip4:192.0.2.1 -all", lookups: 2), T0.AddHours(6));

        Assert.True(changed);
        Assert.Equal(first.DependencyHash, state.PreviousDependencyHash);
        Assert.Contains("198.51.100.0/24", state.PreviousDependencySnapshotJson);
        Assert.Equal(T0.AddHours(6), state.DependencyChangedAtUtc);
        Assert.Equal(1, state.PreviousPublishedLookups);
        Assert.Equal(2, state.PublishedLookups);
        Assert.Equal(T0.AddHours(6), state.LastChangedAtUtc);
    }

    [Fact]
    public void ReobservedHash_DoesNotRefreshTheChangeTimestamp()
    {
        var state = new SpfDriftState { DomainId = Guid.NewGuid() };
        SpfDriftStateCache.Apply(state, Found(), T0);
        var moved = Found(targetRecord: "v=spf1 ip4:192.0.2.1 -all");
        SpfDriftStateCache.Apply(state, moved, T0.AddHours(6));

        var changed = SpfDriftStateCache.Apply(state, moved, T0.AddHours(12));

        Assert.False(changed);
        Assert.Equal(T0.AddHours(6), state.DependencyChangedAtUtc);
    }

    [Fact]
    public void CandidateFlip_SetsPreviousStatusAndTimestamp()
    {
        var state = new SpfDriftState { DomainId = Guid.NewGuid() };
        SpfDriftStateCache.Apply(state, Found(), T0);

        // Same dependency content, but the candidate no longer builds — the
        // safety signal is independent of the dependency hash.
        var refused = Found(candidateStatus: SpfCandidateStatus.Refused, candidate: null);
        var changed = SpfDriftStateCache.Apply(state, refused, T0.AddHours(6));

        Assert.True(changed);
        Assert.Equal(SpfCandidateStatus.Ready, state.PreviousCandidateStatus);
        Assert.Equal(SpfCandidateStatus.Refused, state.CandidateStatus);
        Assert.Equal(T0.AddHours(6), state.CandidateChangedAtUtc);
        Assert.Null(state.PreviousDependencyHash); // hash did not move
    }

    [Fact]
    public async Task RefreshAll_CreatesRowsForActiveDomains_AndSkipsInactive()
    {
        await using var db = new DmarcAnalyzerDbContext(
            new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

        var client = new Client { Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC" };
        var active = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "acme.example", IsActive = true };
        var inactive = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "old.example", IsActive = false };
        db.AddRange(client, active, inactive);
        await db.SaveChangesAsync();

        var txt = new TestDnsTxtResolver()
            .Publish("acme.example", "v=spf1 include:mid.example.com -all")
            .Publish("mid.example.com", "v=spf1 ip4:198.51.100.7 -all");
        var cache = new SpfDriftStateCache(
            db,
            new SpfDriftCheckService(
                new SpfDependencyAnalyzer(txt, new TestDnsMxResolver()),
                new SpfCandidateGenerator(txt, new TestDnsMxResolver(), new TestDnsAddressResolver(),
                    new SpfDependencyAnalyzer(txt, new TestDnsMxResolver()))),
            Options.Create(new SpfDriftOptions()),
            NullLogger<SpfDriftStateCache>.Instance);

        var result = await cache.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Changed);
        Assert.Equal(0, result.Failed);
        var state = Assert.Single(await db.SpfDriftStates.ToListAsync());
        Assert.Equal(active.Id, state.DomainId);
        Assert.Equal(SpfDriftRecordStatus.Found, state.SpfRecordStatus);
        Assert.Equal(SpfCandidateStatus.Ready, state.CandidateStatus);
    }

    [Fact]
    public async Task RefreshAll_CrashAndTimeout_ReadAsLookupFailures()
    {
        await using var db = new DmarcAnalyzerDbContext(
            new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

        var client = new Client { Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC" };
        db.AddRange(
            client,
            new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "crash.example", IsActive = true },
            new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "slow.example", IsActive = true });
        await db.SaveChangesAsync();

        var cache = new SpfDriftStateCache(
            db,
            new FlakyCheckService(),
            Options.Create(new SpfDriftOptions { StartJitterSeconds = 0, PerDomainTimeoutSeconds = 5 }),
            NullLogger<SpfDriftStateCache>.Instance);

        var result = await cache.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(2, result.Checked);
        Assert.Equal(2, result.Failed);
        Assert.All(await db.SpfDriftStates.ToListAsync(),
            s => Assert.Equal(SpfDriftRecordStatus.LookupFailed, s.SpfRecordStatus));
    }

    private sealed class FlakyCheckService : ISpfDriftCheckService
    {
        public async Task<SpfDriftCheckResult> CheckAsync(string domainName, CancellationToken ct, bool bypassCache = false)
        {
            if (domainName.StartsWith("crash", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("boom");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), ct); // the per-domain timeout cancels this
            throw new InvalidOperationException("unreachable");
        }
    }
}
