using System.Net;
using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Analytics.Spf;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class SpfCandidateGeneratorTests
{
    private sealed class FakeTxt(Dictionary<string, IReadOnlyList<string>?> answers) : IDnsTxtResolver
    {
        public readonly HashSet<string> Queried = new(StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false)
        {
            Queried.Add(name);
            return Task.FromResult(answers.GetValueOrDefault(name));
        }
    }

    private sealed class FakeMx(Dictionary<string, IReadOnlyList<MxHost>?> answers) : IDnsMxResolver
    {
        public Task<IReadOnlyList<MxHost>?> ResolveAsync(string domain, CancellationToken ct, bool bypassCache = false)
            => Task.FromResult(answers.GetValueOrDefault(domain));
    }

    private sealed class FakeAddr(Dictionary<string, DnsAddresses?> answers) : IDnsAddressResolver
    {
        public Task<DnsAddresses?> ResolveAsync(string domain, CancellationToken ct, bool bypassCache = false)
            => Task.FromResult(answers.GetValueOrDefault(domain));
    }

    private static DnsAddresses Addr(params string[] ips)
    {
        var parsed = ips.Select(IPAddress.Parse).ToList();
        return new DnsAddresses(
            parsed.Where(p => p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList(),
            parsed.Where(p => p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).ToList());
    }

    private sealed record Fixture(
        SpfCandidateGenerator Generator, FakeTxt Txt, FakeMx Mx, FakeAddr Addr);

    private static Fixture NewFixture(
        Dictionary<string, IReadOnlyList<string>?> txt,
        Dictionary<string, IReadOnlyList<MxHost>?>? mx = null,
        Dictionary<string, DnsAddresses?>? addr = null)
    {
        // The analyzer (before-count) and the generator get separate TXT fakes
        // over shared answers, so tests can assert what the EXPANSION queried —
        // the analyzer follows includes the generator deliberately will not.
        var fakeMx = new FakeMx(mx ?? []);
        var fakeAddr = new FakeAddr(addr ?? []);
        var analyzer = new SpfDependencyAnalyzer(new FakeTxt(txt), fakeMx);
        var fakeTxt = new FakeTxt(txt);
        return new Fixture(new SpfCandidateGenerator(fakeTxt, fakeMx, fakeAddr, analyzer), fakeTxt, fakeMx, fakeAddr);
    }

    [Fact]
    public async Task Generate_StaticChain_ExpandsAndSorts()
    {
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 include:mid.example.com ip4:192.0.2.10 -all"],
            ["mid.example.com"] = ["v=spf1 ip4:198.51.100.0/24 ip4:192.0.2.2 -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        // Spliced in place, record order kept (order is match semantics).
        Assert.Equal(
            "v=spf1 ip4:198.51.100.0/24 ip4:192.0.2.2 ip4:192.0.2.10 -all",
            result.Candidate);
        Assert.Equal(1, result.OriginalLookups);
        Assert.Equal(0, result.CandidateLookups);
    }

    [Theory]
    [InlineData("_spf.google.com")]
    [InlineData("spf.protection.outlook.com")]
    public async Task Generate_StubProviders_PreservedAndNeverQueried(string stub)
    {
        // Microsoft 365 and Google as stub includes (fixture-shaped, never live):
        // volatile sets stay verbatim and cost their lookup in the candidate.
        var fixture = NewFixture(new()
        {
            ["acme.example"] = [$"v=spf1 include:{stub} include:static.example.com ip4:192.0.2.1 -all"],
            ["static.example.com"] = ["v=spf1 ip4:198.51.100.7 -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        Assert.Equal(
            $"v=spf1 include:{stub} ip4:198.51.100.7 ip4:192.0.2.1 -all",
            result.Candidate);
        Assert.DoesNotContain(stub, fixture.Txt.Queried);
        var kept = result.Terms.Single(t => t.OriginalText == $"include:{stub}");
        Assert.Equal(SpfCandidateTermOutcome.Preserved, kept.Outcome);
        Assert.Contains("stub", kept.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.OriginalLookups);
        Assert.Equal(1, result.CandidateLookups);
    }

    [Fact]
    public async Task Generate_AllDynamic_RefusesWithKeptReasons()
    {
        // Nothing expandable and nothing gained — the candidate would be
        // identical, so refuse with the kept terms' reasons, not "already flat".
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 exists:%{i}.example.com ptr -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Refused, result.Status);
        Assert.Null(result.Candidate);
        Assert.Contains(result.Reasons, r => r.Contains("Nothing could be expanded", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, r => r.Contains("exists:%{i}.example.com", StringComparison.Ordinal));
        Assert.Equal(2, result.Terms.Count(t => t.Outcome == SpfCandidateTermOutcome.Preserved));
    }

    [Fact]
    public async Task Generate_PartiallyDynamic_ExpandsStaticKeepsDynamic()
    {
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 exists:%{i}.example.com include:static.example.com -all"],
            ["static.example.com"] = ["v=spf1 ip4:198.51.100.7 -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        Assert.Equal("v=spf1 exists:%{i}.example.com ip4:198.51.100.7 -all", result.Candidate);
        Assert.Equal(2, result.OriginalLookups);
        Assert.Equal(1, result.CandidateLookups);
    }

    [Fact]
    public async Task Generate_Redirect_FollowsThroughWithTargetAll()
    {
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 mx redirect=target.example.com"],
            ["target.example.com"] = ["v=spf1 ip4:198.51.100.0/24 ~all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        // Local mx dropped (ignored under redirect); target's all wins.
        Assert.Equal("v=spf1 ip4:198.51.100.0/24 ~all", result.Candidate);
        var mx = result.Terms.Single(t => t.OriginalText == "mx");
        Assert.Equal(SpfCandidateTermOutcome.Dropped, mx.Outcome);
    }

    [Fact]
    public async Task Generate_Mx_ExpandsExchangesWithCidr()
    {
        var fixture = NewFixture(
            new() { ["acme.example"] = ["v=spf1 mx:mail.example.com/24 -all"] },
            new() { ["mail.example.com"] = [new MxHost(10, "mx1.example.com"), new MxHost(20, "mx2.example.com")] },
            new()
            {
                ["mx1.example.com"] = Addr("192.0.2.10", "192.0.2.2"),
                ["mx2.example.com"] = Addr("192.0.2.20"),
            });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        Assert.Equal(
            "v=spf1 ip4:192.0.2.2/24 ip4:192.0.2.10/24 ip4:192.0.2.20/24 -all",
            result.Candidate);
    }

    [Fact]
    public async Task Generate_QualifiedMx_AppliesQualifierPerAddress()
    {
        var fixture = NewFixture(
            new() { ["acme.example"] = ["v=spf1 -mx -all"] },
            new() { ["acme.example"] = [new MxHost(10, "mx.example.com")] },
            new() { ["mx.example.com"] = Addr("192.0.2.5") });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal("v=spf1 -ip4:192.0.2.5 -all", result.Candidate);
    }

    [Fact]
    public async Task Generate_Amechanism_ExpandsBothFamiliesWithDualCidr()
    {
        var fixture = NewFixture(
            new() { ["acme.example"] = ["v=spf1 a:hosts.example.com/24//64 -all"] },
            addr: new() { ["hosts.example.com"] = Addr("192.0.2.9", "2001:db8::9") });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal("v=spf1 ip4:192.0.2.9/24 ip6:2001:db8::9/64 -all", result.Candidate);
    }

    [Fact]
    public async Task Generate_LookupFailure_PreservesWholeTermUnverified()
    {
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 include:flaky.example.com include:static.example.com -all"],
            ["flaky.example.com"] = null, // SERVFAIL — unknown, not missing
            ["static.example.com"] = ["v=spf1 ip4:198.51.100.7 -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        Assert.Equal("v=spf1 include:flaky.example.com ip4:198.51.100.7 -all", result.Candidate);
        var kept = result.Terms.Single(t => t.OriginalText == "include:flaky.example.com");
        Assert.Equal(SpfCandidateTermOutcome.Preserved, kept.Outcome);
        Assert.Contains("unverified", kept.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.CandidateLookups);
    }

    [Fact]
    public async Task Generate_MxPartialFailure_PreservesWholeTerm()
    {
        // One unresolvable exchange: all-or-nothing keeps the mx, never half-expands.
        var fixture = NewFixture(
            new() { ["acme.example"] = ["v=spf1 mx -all"] },
            new() { ["acme.example"] = [new MxHost(10, "good.example.com"), new MxHost(20, "bad.example.com")] },
            new() { ["good.example.com"] = Addr("192.0.2.1"), ["bad.example.com"] = null });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        // Nothing expanded — the candidate would be identical, so refuse with the cause.
        Assert.Equal(SpfCandidateStatus.Refused, result.Status);
        Assert.Contains(result.Reasons, r => r.Contains("bad.example.com", StringComparison.Ordinal));
        var kept = result.Terms.Single(t => t.OriginalText == "mx");
        Assert.Equal(SpfCandidateTermOutcome.Preserved, kept.Outcome);
    }

    [Fact]
    public async Task Generate_Cycle_PreservedAsIs()
    {
        var fixture = NewFixture(new()
        {
            ["a.example.com"] = ["v=spf1 include:b.example.com -all"],
            ["b.example.com"] = ["v=spf1 include:a.example.com ip4:192.0.2.1 -all"],
        });

        var result = await fixture.Generator.GenerateAsync("a.example.com", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        Assert.Equal("v=spf1 include:a.example.com ip4:192.0.2.1 -all", result.Candidate);
    }

    [Fact]
    public async Task Generate_QualifiedInclude_PreservedWhole()
    {
        // -include: matches with fail — addresses could not reproduce that.
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 -include:block.example.com include:static.example.com -all"],
            ["block.example.com"] = ["v=spf1 ip4:198.51.100.0/24 -all"],
            ["static.example.com"] = ["v=spf1 ip4:192.0.2.1 -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        Assert.Equal("v=spf1 -include:block.example.com ip4:192.0.2.1 -all", result.Candidate);
        var kept = result.Terms.Single(t => t.OriginalText == "-include:block.example.com");
        Assert.Equal(SpfCandidateTermOutcome.Preserved, kept.Outcome);
    }

    [Fact]
    public async Task Generate_NestedNonPlus_DroppedAsUnmatchable()
    {
        // -ip4 inside an include can never yield a pass through it — dropped, counted.
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 include:inner.example.com -all"],
            ["inner.example.com"] = ["v=spf1 ip4:192.0.2.1 -ip4:192.0.2.2 -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal("v=spf1 ip4:192.0.2.1 -all", result.Candidate);
        var expanded = result.Terms.Single(t => t.Outcome == SpfCandidateTermOutcome.Expanded);
        Assert.Contains("1 nested term(s)", expanded.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_AnswerOrder_DoesNotChangeCandidate()
    {
        // DNS answer order varies (round-robin) — the candidate must not.
        var txt = new Dictionary<string, IReadOnlyList<string>?>
        {
            ["acme.example"] = ["v=spf1 a -all"],
        };
        var forward = NewFixture(txt, addr: new()
        {
            ["acme.example"] = Addr("192.0.2.1", "192.0.2.2", "192.0.2.3"),
        });
        var reverse = NewFixture(txt, addr: new()
        {
            ["acme.example"] = Addr("192.0.2.3", "192.0.2.2", "192.0.2.1"),
        });

        var first = await forward.Generator.GenerateAsync("acme.example", CancellationToken.None);
        var second = await reverse.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(first.Candidate, second.Candidate);
        Assert.Equal("v=spf1 ip4:192.0.2.1 ip4:192.0.2.2 ip4:192.0.2.3 -all", first.Candidate);
    }

    [Fact]
    public async Task Generate_ReportsSizeSegmentsAndLookupDelta()
    {
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 include:big.example.com include:_spf.google.com -all"],
            ["big.example.com"] = ["v=spf1 " + string.Join(' ',
                Enumerable.Range(1, 20).Select(i => $"ip4:198.51.100.{i}")) + " -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Ready, result.Status);
        Assert.Equal(result.Candidate!.Length, result.CandidateLength);
        Assert.Equal((result.CandidateLength + 254) / 255, result.TxtSegments);
        Assert.True(result.TxtSegments >= 2, "20 addresses must need multi-string TXT");
        Assert.Equal(2, result.OriginalLookups);
        Assert.Equal(1, result.CandidateLookups); // only the google stub still costs
    }

    [Fact]
    public async Task Generate_AllDynamic_Refuses()
    {
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 exists:%{i}.example.com -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Refused, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task Generate_AlreadyFlat_Refuses()
    {
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 ip4:192.0.2.0/24 -all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Refused, result.Status);
        Assert.Contains(result.Reasons, r => r.Contains("Already flat", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)] // lookup failure — no answers entry reads as null
    [InlineData("")] // NXDOMAIN — explicit empty tested separately below
    public async Task Generate_MissingOrFailedRoot_Refuses(string? marker)
    {
        var answers = new Dictionary<string, IReadOnlyList<string>?>();
        if (marker == "")
        {
            answers["acme.example"] = Array.Empty<string>();
        }

        var result = await NewFixture(answers).Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Refused, result.Status);
        Assert.Null(result.Candidate);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public async Task Generate_MultipleRecords_Refuses()
    {
        var fixture = NewFixture(new()
        {
            ["acme.example"] = ["v=spf1 -all", "v=spf1 ~all"],
        });

        var result = await fixture.Generator.GenerateAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfCandidateStatus.Refused, result.Status);
        Assert.Contains(result.Reasons, r => r.Contains("reject all", StringComparison.OrdinalIgnoreCase));
    }
}
