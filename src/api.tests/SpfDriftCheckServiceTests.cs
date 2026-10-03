using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Analytics.Spf;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class SpfDriftCheckServiceTests
{
    private static SpfDriftCheckService Service(
        IDnsTxtResolver txt, TestDnsMxResolver? mx = null, TestDnsAddressResolver? addr = null)
    {
        var mxResolver = mx ?? new TestDnsMxResolver();
        return new SpfDriftCheckService(
            new SpfDependencyAnalyzer(txt, mxResolver),
            new SpfCandidateGenerator(txt, mxResolver, addr ?? new TestDnsAddressResolver(),
                new SpfDependencyAnalyzer(txt, mxResolver)));
    }

    [Fact]
    public async Task Found_SnapshotsTargetsAndHashesDeterministically()
    {
        var txt = new TestDnsTxtResolver()
            .Publish("acme.example", "v=spf1 include:mid.example.com -all")
            .Publish("mid.example.com", "v=spf1 ip4:198.51.100.0/24 -all");

        var result = await Service(txt).CheckAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfDriftRecordStatus.Found, result.SpfRecordStatus);
        Assert.Equal("v=spf1 include:mid.example.com -all", result.RawRecord);
        var entry = Assert.Single(result.Dependencies);
        Assert.Equal("mid.example.com", entry.Domain);
        Assert.Equal("v=spf1 ip4:198.51.100.0/24 -all", entry.Record);
        Assert.NotNull(entry.Hash);
        Assert.Equal(
            SpfDriftCheckService.HashSnapshot(result.RawRecord!, result.Dependencies),
            result.DependencyHash);
        Assert.Equal(SpfCandidateStatus.Ready, result.Candidate!.Status);
        Assert.Equal(SpfDriftCheckService.HashText(result.Candidate.Candidate!), result.CandidateHash);
        Assert.Equal(1, result.PublishedLookups);
        Assert.False(result.PublishedOverBudget);
    }

    [Fact]
    public async Task MissingRoot_MapsToMissing()
    {
        var result = await Service(new TestDnsTxtResolver())
            .CheckAsync("bare.example", CancellationToken.None);

        Assert.Equal(SpfDriftRecordStatus.Missing, result.SpfRecordStatus);
        Assert.Null(result.RawRecord);
        Assert.Empty(result.Dependencies);
        Assert.Null(result.DependencyHash);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task FailedRoot_MapsToLookupFailed()
    {
        var txt = new TestDnsTxtResolver().FailFor("flaky.example");

        var result = await Service(txt).CheckAsync("flaky.example", CancellationToken.None);

        Assert.Equal(SpfDriftRecordStatus.LookupFailed, result.SpfRecordStatus);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task MultipleRecords_MapToInvalid()
    {
        var txt = new TestDnsTxtResolver()
            .Publish("messy.example", "v=spf1 -all", "v=spf1 ~all");

        var result = await Service(txt).CheckAsync("messy.example", CancellationToken.None);

        Assert.Equal(SpfDriftRecordStatus.Invalid, result.SpfRecordStatus);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task UnresolvableBranch_SnapshotsWithNullRecord()
    {
        // The target failed mid-walk: the snapshot records the gap (null)
        // rather than dropping the target, so a later success reads as drift.
        var txt = new TestDnsTxtResolver()
            .Publish("acme.example", "v=spf1 include:gone.example.com -all")
            .FailFor("gone.example.com");

        var result = await Service(txt).CheckAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfDriftRecordStatus.Found, result.SpfRecordStatus);
        var entry = Assert.Single(result.Dependencies);
        Assert.Equal("gone.example.com", entry.Domain);
        Assert.Null(entry.Record);
        Assert.Null(entry.Hash);
    }

    [Fact]
    public async Task RepeatedTarget_SnapshotsOnce()
    {
        var txt = new TestDnsTxtResolver()
            .Publish("acme.example", "v=spf1 include:mid.example.com include:mid.example.com -all")
            .Publish("mid.example.com", "v=spf1 ip4:198.51.100.7 -all");

        var result = await Service(txt).CheckAsync("acme.example", CancellationToken.None);

        Assert.Single(result.Dependencies);
    }

    [Fact]
    public void HashSnapshot_IgnoresCaseAndWhitespace()
    {
        var deps = (IReadOnlyList<SpfDependencySnapshotEntry>)
        [
            new("MID.example.com", "v=spf1  ip4:198.51.100.7   -all", "ignored"),
        ];

        Assert.Equal(
            SpfDriftCheckService.HashSnapshot("v=spf1 include:mid.example.com -all", deps),
            SpfDriftCheckService.HashSnapshot("V=SPF1  include:mid.example.com   -all", deps));
    }

    [Fact]
    public async Task BypassCache_ThreadsThroughEveryLookup()
    {
        var txt = new RecordingTxtResolver()
            .Publish("acme.example", "v=spf1 include:mid.example.com -all")
            .Publish("mid.example.com", "v=spf1 ip4:198.51.100.7 -all");

        await Service(txt).CheckAsync("acme.example", CancellationToken.None, bypassCache: true);

        Assert.NotEmpty(txt.Bypassed);
        Assert.All(txt.Bypassed, b => Assert.True(b));
    }

    [Fact]
    public async Task ThrowingAnalyzer_ReadsAsLookupFailed()
    {
        var service = new SpfDriftCheckService(
            new ThrowingAnalyzer(),
            new SpfCandidateGenerator(new TestDnsTxtResolver(), new TestDnsMxResolver(),
                new TestDnsAddressResolver(), new ThrowingAnalyzer()));

        var result = await service.CheckAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfDriftRecordStatus.LookupFailed, result.SpfRecordStatus);
        Assert.Contains(result.Issues, i => i.Contains("unexpectedly", StringComparison.Ordinal));
    }

    private sealed class ThrowingAnalyzer : ISpfDependencyAnalyzer
    {
        public Task<SpfAnalysis> AnalyzeAsync(string domainName, CancellationToken ct, bool bypassCache = false)
            => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingTxtResolver : IDnsTxtResolver
    {
        private readonly Dictionary<string, IReadOnlyList<string>?> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public readonly List<bool> Bypassed = [];

        public RecordingTxtResolver Publish(string name, params string[] txts)
        {
            _byName[name] = txts;
            return this;
        }

        public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false)
        {
            Bypassed.Add(bypassCache);
            return Task.FromResult(_byName.TryGetValue(name, out var txts) ? txts : Array.Empty<string>());
        }
    }
}
