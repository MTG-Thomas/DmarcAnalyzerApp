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
/// The drift panel's read/recheck seam: Get is a tenancy-checked database read,
/// Recheck runs a live check with the resolver cache bypassed and persists it.
/// </summary>
public sealed class SpfDriftInspectionServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static (Client Client, Domain Domain) Seed(DmarcAnalyzerDbContext db, string slug = "acme")
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC",
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = $"{slug}.example", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.AddRange(client, domain);
        return (client, domain);
    }

    private static SpfDriftInspectionService Service(
        DmarcAnalyzerDbContext db, TestCurrentUserContext user, IDnsTxtResolver txt)
    {
        var mx = new TestDnsMxResolver();
        var analyzer = new SpfDependencyAnalyzer(txt, mx);
        return new SpfDriftInspectionService(
            db,
            user,
            new SpfDriftCheckService(
                analyzer,
                new SpfCandidateGenerator(txt, mx, new TestDnsAddressResolver(), analyzer)),
            new SpfDriftStateCache(
                db,
                new SpfDriftCheckService(
                    analyzer,
                    new SpfCandidateGenerator(txt, mx, new TestDnsAddressResolver(), analyzer)),
                Options.Create(new SpfDriftOptions()),
                NullLogger<SpfDriftStateCache>.Instance));
    }

    [Fact]
    public async Task Get_BeforeFirstCheck_ReportsUnchecked()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        await db.SaveChangesAsync();

        var dto = await Service(db, TestCurrentUserContext.Admin(), new TestDnsTxtResolver())
            .GetAsync(domain.Id, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.False(dto.Checked);
        Assert.Null(dto.SpfRecordStatus);
        Assert.Empty(dto.Dependencies);
        Assert.Null(dto.LastCheckedAtUtc);
    }

    [Fact]
    public async Task Get_MapsState_IncludingPreviousValuesAndFreshness()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        db.Add(new SpfDriftState
        {
            DomainId = domain.Id,
            SpfRecordStatus = SpfDriftRecordStatus.Found,
            RawRecord = "v=spf1 include:mid.example.com -all",
            DependencySnapshotJson = """[{"domain":"mid.example.com","record":"v=spf1 ip4:192.0.2.1 -all","hash":"aa"}]""",
            DependencyHash = "new",
            PreviousDependencySnapshotJson = """[{"domain":"mid.example.com","record":"v=spf1 ip4:198.51.100.0/24 -all","hash":"bb"}]""",
            PreviousDependencyHash = "old",
            DependencyChangedAtUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            CandidateStatus = SpfCandidateStatus.Ready,
            PublishedLookups = 2,
            PreviousPublishedLookups = 1,
            PublishedOverBudget = false,
            PreviousPublishedOverBudget = false,
            LastCheckedAtUtc = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc),
            LastSuccessAtUtc = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc),
            ConsecutiveFailures = 0,
        });
        await db.SaveChangesAsync();

        var dto = await Service(db, TestCurrentUserContext.Admin(), new TestDnsTxtResolver())
            .GetAsync(domain.Id, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.True(dto.Checked);
        Assert.Equal("v=spf1 include:mid.example.com -all", dto.RawRecord);
        Assert.Contains("192.0.2.1", Assert.Single(dto.Dependencies).Record);
        Assert.Contains("198.51.100.0/24", Assert.Single(dto.PreviousDependencies).Record);
        Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), dto.DependencyChangedAtUtc);
        Assert.Equal(2, dto.PublishedLookups);
        Assert.Equal(1, dto.PreviousPublishedLookups);
        Assert.Equal(0, dto.ConsecutiveFailures);
    }

    [Fact]
    public async Task Get_CrossTenantDomain_ReadsAsNotFound()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        await db.SaveChangesAsync();

        // Viewer granted a different client must not learn the domain exists.
        var dto = await Service(db, TestCurrentUserContext.Viewer(Guid.NewGuid()), new TestDnsTxtResolver())
            .GetAsync(domain.Id, CancellationToken.None);

        Assert.Null(dto);
    }

    [Fact]
    public async Task Get_UnknownDomain_ReturnsNull()
    {
        await using var db = NewDb();

        var dto = await Service(db, TestCurrentUserContext.Admin(), new TestDnsTxtResolver())
            .GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(dto);
    }

    [Fact]
    public async Task Recheck_RunsLiveCheck_BypassesCache_AndPersists()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        await db.SaveChangesAsync();

        var txt = new RecordingBypassTxtResolver()
            .Publish("acme.example", "v=spf1 include:mid.example.com -all")
            .Publish("mid.example.com", "v=spf1 ip4:198.51.100.7 -all");

        var dto = await Service(db, TestCurrentUserContext.Admin(), txt)
            .RecheckAsync(domain.Id, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.True(dto.Checked);
        Assert.Equal(SpfDriftRecordStatus.Found, dto.SpfRecordStatus);
        Assert.Equal(SpfCandidateStatus.Ready, dto.CandidateStatus);
        Assert.NotEmpty(txt.Bypassed);
        Assert.All(txt.Bypassed, Assert.True);

        // Persisted: a fresh read sees the row without another live check.
        var reread = await Service(db, TestCurrentUserContext.Admin(), new TestDnsTxtResolver())
            .GetAsync(domain.Id, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.True(reread.Checked);
        Assert.Equal(SpfDriftRecordStatus.Found, reread.SpfRecordStatus);
    }

    [Fact]
    public async Task Recheck_CrossTenantDomain_ReturnsNull_WithoutTouchingDns()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        await db.SaveChangesAsync();

        var txt = new RecordingBypassTxtResolver();
        var dto = await Service(db, TestCurrentUserContext.Viewer(Guid.NewGuid()), txt)
            .RecheckAsync(domain.Id, CancellationToken.None);

        Assert.Null(dto);
        Assert.Empty(txt.Bypassed);
        Assert.Empty(await db.SpfDriftStates.ToListAsync());
    }

    private sealed class RecordingBypassTxtResolver : IDnsTxtResolver
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public readonly List<bool> Bypassed = [];

        public RecordingBypassTxtResolver Publish(string name, params string[] txts)
        {
            _byName[name] = txts;
            return this;
        }

        public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false)
        {
            Bypassed.Add(bypassCache);
            return Task.FromResult<IReadOnlyList<string>?>(
                _byName.TryGetValue(name, out var txts) ? txts : []);
        }
    }
}
