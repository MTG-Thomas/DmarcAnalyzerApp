using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Analytics.Spf;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class SpfDriftPersistenceIntegrationTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task SpfDriftState_DomainIdUnique_RejectsSecondRow()
    {
        var domainId = await ResetMigrateAndSeedAsync();

        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();

        firstDb.SpfDriftStates.Add(new SpfDriftState
        {
            DomainId = domainId,
            SpfRecordStatus = SpfDriftRecordStatus.Found,
            LastCheckedAtUtc = DateTime.UtcNow,
        });
        await firstDb.SaveChangesAsync();

        secondDb.SpfDriftStates.Add(new SpfDriftState
        {
            DomainId = domainId,
            SpfRecordStatus = SpfDriftRecordStatus.Found,
            LastCheckedAtUtc = DateTime.UtcNow,
        });

        // The 1:1 the cache relies on: RefreshAll folds into a dictionary by
        // DomainId and ApplyAsync reads with SingleOrDefault, so a second row
        // must be impossible rather than merely unexpected.
        await Assert.ThrowsAsync<DbUpdateException>(() => secondDb.SaveChangesAsync());
    }

    [Fact]
    public async Task SpfDriftRefresh_RunTwice_KeepsOneRowPerDomain()
    {
        await ResetMigrateAndSeedAsync();

        // Two passes with fresh contexts and fresh caches, as two worker
        // restarts would: the second pass must fold into the existing row,
        // never insert beside it.
        for (var pass = 0; pass < 2; pass++)
        {
            await using var db = database.CreateDbContext();
            var cache = new SpfDriftStateCache(
                db,
                new StubCheckService(),
                Options.Create(new SpfDriftOptions { StartJitterSeconds = 0 }),
                NullLogger<SpfDriftStateCache>.Instance);

            var result = await cache.RefreshAllAsync(CancellationToken.None);
            Assert.Equal(1, result.Checked);
            Assert.Equal(0, result.Failed);
        }

        await using var verification = database.CreateDbContext();
        var state = Assert.Single(await verification.SpfDriftStates.ToListAsync());
        Assert.Equal(SpfDriftRecordStatus.Found, state.SpfRecordStatus);
        Assert.Equal(SpfCandidateStatus.Ready, state.CandidateStatus);
        Assert.NotNull(state.DependencyHash);
    }

    private async Task<Guid> ResetMigrateAndSeedAsync()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        var client = new Client
        {
            Name = "SPF drift fixture",
            Slug = $"spf-drift-{Guid.NewGuid():N}",
            Timezone = "UTC",
        };
        var domain = new Domain
        {
            ClientId = client.Id,
            Name = "drift.example",
            IsActive = true,
        };

        await using var db = database.CreateDbContext();
        db.AddRange(client, domain);
        await db.SaveChangesAsync();

        return domain.Id;
    }

    /// <summary>Deterministic DNS-free check: a stable found record with a ready candidate.</summary>
    private sealed class StubCheckService : ISpfDriftCheckService
    {
        public Task<SpfDriftCheckResult> CheckAsync(string domainName, CancellationToken ct, bool bypassCache = false)
        {
            var deps = (IReadOnlyList<SpfDependencySnapshotEntry>)
                [new("mid.example.com", "v=spf1 ip4:198.51.100.0/24 -all", "hash")];
            const string raw = "v=spf1 include:mid.example.com -all";
            const string candidate = "v=spf1 ip4:198.51.100.0/24 -all";
            var dto = new SpfCandidateDto(
                SpfCandidateStatus.Ready, raw, candidate, [], [], 1, 0, candidate.Length, 1);
            return Task.FromResult(new SpfDriftCheckResult(
                SpfDriftRecordStatus.Found, raw, deps,
                SpfDriftCheckService.HashSnapshot(raw, deps), dto,
                SpfDriftCheckService.HashText(candidate), 1, false, []));
        }
    }
}
