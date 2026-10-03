using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Analytics.Spf;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class SpfDependencyAnalyzerTests
{
    private sealed class FakeTxt(Dictionary<string, IReadOnlyList<string>?> answers) : IDnsTxtResolver
    {
        public int Queries;
        public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false)
        {
            Queries++;
            return Task.FromResult(answers.GetValueOrDefault(name));
        }
    }

    private sealed class FakeMx(Dictionary<string, IReadOnlyList<MxHost>?> answers) : IDnsMxResolver
    {
        public int Queries;
        public Task<IReadOnlyList<MxHost>?> ResolveAsync(string domain, CancellationToken ct, bool bypassCache = false)
        {
            Queries++;
            return Task.FromResult(answers.GetValueOrDefault(domain));
        }
    }

    private static SpfDependencyAnalyzer NewAnalyzer(
        Dictionary<string, IReadOnlyList<string>?> txt,
        Dictionary<string, IReadOnlyList<MxHost>?>? mx = null)
        => new(new FakeTxt(txt), new FakeMx(mx ?? []));

    [Fact]
    public async Task Analyze_NestedIncludes_ShareOneGlobalBudget()
    {
        var analyzer = NewAnalyzer(new()
        {
            ["acme.example"] = ["v=spf1 include:b.example.com include:c.example.com -all"],
            ["b.example.com"] = ["v=spf1 ip4:198.51.100.0/24 include:d.example.com -all"],
            ["c.example.com"] = ["v=spf1 mx -all"],
            ["d.example.com"] = ["v=spf1 a -all"],
        },
        new() { ["c.example.com"] = [new MxHost(10, "mail.c.example.com")] });

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        // 2 root includes + 1 nested include + 1 mx + 1 a = 5. Not 2, not per-level.
        Assert.Equal(5, result.TotalLookups);
        Assert.False(result.OverBudget);
        Assert.Equal(0, result.VoidLookups);
        Assert.Equal(SpfNodeStatus.Found, result.Root.Status);
        Assert.Equal(2, result.Root.Terms.Count(t => t.Resolution is not null));

        var b = result.Root.Terms.Single(t => t.Target == "b.example.com").Resolution!;
        Assert.Equal(SpfNodeStatus.Found, b.Status);
        Assert.Equal(2, b.LookupsUsed); // its own include's 1 plus d's a
        Assert.Equal("d.example.com",
            b.Terms.Single(t => t.Kind == SpfTermKind.Include).Resolution!.Domain);
    }

    [Fact]
    public async Task Analyze_Redirect_FollowsTargetAndIgnoresLocalMechanisms()
    {
        var analyzer = NewAnalyzer(new()
        {
            ["acme.example"] = ["v=spf1 mx redirect=target.example.com"],
            ["target.example.com"] = ["v=spf1 ip4:198.51.100.0/24 -all"],
        });

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        // Only the redirect costs; the mx beside it is ignored, not charged.
        Assert.Equal(1, result.TotalLookups);
        var mx = result.Root.Terms.Single(t => t.Kind == SpfTermKind.Mx);
        Assert.Contains("Ignored", mx.Note, StringComparison.Ordinal);
        var redirect = result.Root.Terms.Single(t => t.Kind == SpfTermKind.Redirect);
        Assert.Equal(SpfNodeStatus.Found, redirect.Resolution!.Status);
    }

    [Fact]
    public async Task Analyze_Cycle_FollowedOnceWithoutExtraQueries()
    {
        var txt = new FakeTxt(new()
        {
            ["a.example.com"] = ["v=spf1 include:b.example.com -all"],
            ["b.example.com"] = ["v=spf1 include:a.example.com -all"],
        });
        var analyzer = new SpfDependencyAnalyzer(txt, new FakeMx([]));

        var result = await analyzer.AnalyzeAsync("a.example.com", CancellationToken.None);

        Assert.Equal(2, result.TotalLookups); // a→b charged, b→a charged, revisit free
        Assert.Equal(2, txt.Queries); // the revisit is recognized before any query
        var revisit = result.Root.Terms.Single(t => t.Kind == SpfTermKind.Include).Resolution!
            .Terms.Single(t => t.Kind == SpfTermKind.Include).Resolution!;
        Assert.Equal(SpfNodeStatus.Cycle, revisit.Status);
        Assert.Contains(result.Issues, i => i.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Analyze_SelfInclude_IsACycle()
    {
        var analyzer = NewAnalyzer(new()
        {
            ["a.example.com"] = ["v=spf1 include:a.example.com -all"],
        });

        var result = await analyzer.AnalyzeAsync("a.example.com", CancellationToken.None);

        var child = result.Root.Terms.Single(t => t.Kind == SpfTermKind.Include).Resolution!;
        Assert.Equal(SpfNodeStatus.Cycle, child.Status);
    }

    [Fact]
    public async Task Analyze_MissingInclude_CountsVoidLookup()
    {
        var analyzer = NewAnalyzer(new()
        {
            ["acme.example"] = ["v=spf1 include:ghost.example.com -all"],
            // Explicit empty: NXDOMAIN — a void lookup, not a failure.
            ["ghost.example.com"] = Array.Empty<string>(),
        });

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(1, result.TotalLookups);
        Assert.Equal(1, result.VoidLookups);
        Assert.False(result.OverBudget);
        var child = result.Root.Terms.Single(t => t.Kind == SpfTermKind.Include).Resolution!;
        Assert.Equal(SpfNodeStatus.Missing, child.Status);
    }

    [Fact]
    public async Task Analyze_ThirdVoidLookup_AbortsWithPermerrorOutcome()
    {
        var analyzer = NewAnalyzer(new()
        {
            ["acme.example"] = ["v=spf1 include:g1.example.com include:g2.example.com include:g3.example.com include:real.example.com -all"],
            ["g1.example.com"] = Array.Empty<string>(),
            ["g2.example.com"] = Array.Empty<string>(),
            ["g3.example.com"] = Array.Empty<string>(),
            ["real.example.com"] = ["v=spf1 ip4:198.51.100.1 -all"],
        });

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(3, result.VoidLookups);
        Assert.Contains(result.Issues, i => i.Contains("void", StringComparison.OrdinalIgnoreCase));
        // Receivers stop at the third void — the fourth include is never visited.
        var fourth = result.Root.Terms.Single(t => t.Target == "real.example.com");
        Assert.Null(fourth.Resolution);
        Assert.Contains("budget already ran out", fourth.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_EleventhLookup_TripsOverBudgetAndNamesTheTerm()
    {
        var answers = new Dictionary<string, IReadOnlyList<string>?>
        {
            ["acme.example"] = ["v=spf1 " + string.Join(' ',
                Enumerable.Range(0, 11).Select(i => $"include:i{i}.example.com")) + " -all"],
        };
        foreach (var i in Enumerable.Range(0, 11))
        {
            answers[$"i{i}.example.com"] = ["v=spf1 ip4:198.51.100.1 -all"];
        }

        var result = await NewAnalyzer(answers).AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.True(result.OverBudget);
        Assert.Equal(11, result.TotalLookups);
        Assert.Equal("include:i10.example.com", result.OverBudgetTerm);
        Assert.Contains(result.Issues, i => i.Contains("permerror", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Analyze_LookupFailure_BranchUnknownSiblingsContinue()
    {
        var analyzer = NewAnalyzer(new()
        {
            ["acme.example"] = ["v=spf1 include:flaky.example.com include:ok.example.com -all"],
            // Explicit null: SERVFAIL/timeout — unknown, not missing, not void.
            ["flaky.example.com"] = null,
            ["ok.example.com"] = ["v=spf1 ip4:198.51.100.1 -all"],
        });

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(0, result.VoidLookups); // failures are not voids
        var flaky = result.Root.Terms.Single(t => t.Target == "flaky.example.com").Resolution!;
        Assert.Equal(SpfNodeStatus.LookupFailed, flaky.Status);
        var ok = result.Root.Terms.Single(t => t.Target == "ok.example.com").Resolution!;
        Assert.Equal(SpfNodeStatus.Found, ok.Status);
        Assert.Contains(result.Issues, i => i.Contains("flaky.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_MultipleRecords_IsPermerrorWithoutDescent()
    {
        var txt = new FakeTxt(new()
        {
            ["acme.example"] = ["v=spf1 include:b.example.com -all", "v=spf1 -all"],
            ["b.example.com"] = ["v=spf1 ip4:198.51.100.1 -all"],
        });
        var analyzer = new SpfDependencyAnalyzer(txt, new FakeMx([]));

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfNodeStatus.Permerror, result.Root.Status);
        Assert.Equal(0, result.TotalLookups);
        Assert.Equal(1, txt.Queries); // nothing followed
        Assert.Contains(result.Root.Issues, i => i.Contains("permerror", StringComparison.OrdinalIgnoreCase));
        // The first record still parses for display so the fix is visible.
        Assert.NotEmpty(result.Root.Terms);
    }

    [Fact]
    public async Task Analyze_MacroInclude_NotFollowedAndFlaggedDynamic()
    {
        var txt = new FakeTxt(new()
        {
            ["acme.example"] = ["v=spf1 include:%{i}._spf.example.com -all"],
        });
        var analyzer = new SpfDependencyAnalyzer(txt, new FakeMx([]));

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        var term = result.Root.Terms.Single(t => t.Kind == SpfTermKind.Include);
        Assert.True(term.IsDynamic);
        Assert.Null(term.Resolution);
        Assert.Contains("Macro", term.Note, StringComparison.Ordinal);
        Assert.Equal(1, txt.Queries); // root only
    }

    [Fact]
    public async Task Analyze_MxTerm_ListsHostsCappedWithTotal()
    {
        var hosts = Enumerable.Range(0, 23)
            .Select(i => new MxHost(10 + i, $"mx{i}.example.com"))
            .ToList();
        var analyzer = NewAnalyzer(
            new() { ["acme.example"] = ["v=spf1 mx -all"] },
            new() { ["acme.example"] = hosts });

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(1, result.TotalLookups); // the mechanism costs 1 whatever it finds
        var term = result.Root.Terms.Single(t => t.Kind == SpfTermKind.Mx);
        Assert.Equal(10, term.MxHosts.Count);
        Assert.Equal(23, term.MxHostTotal);
    }

    [Fact]
    public async Task Analyze_MxWithoutRecords_CountsVoidLookup()
    {
        var analyzer = NewAnalyzer(
            new() { ["acme.example"] = ["v=spf1 mx -all"] },
            new() { ["acme.example"] = Array.Empty<MxHost>() });

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(1, result.VoidLookups);
        var term = result.Root.Terms.Single(t => t.Kind == SpfTermKind.Mx);
        Assert.Contains("void", term.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Analyze_MissingRoot_IsMissingWithoutVoids()
    {
        var result = await NewAnalyzer(new()
        {
            ["acme.example"] = Array.Empty<string>(),
        }).AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfNodeStatus.Missing, result.Root.Status);
        Assert.Equal(0, result.TotalLookups);
        Assert.Equal(0, result.VoidLookups); // a missing root is "none", not a void mechanism
    }

    [Fact]
    public async Task Analyze_FailedRoot_IsLookupFailed()
    {
        var result = await NewAnalyzer(new()
        {
            ["acme.example"] = null,
        }).AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfNodeStatus.LookupFailed, result.Root.Status);
    }

    [Fact]
    public async Task Analyze_WideFan_TerminatesBounded()
    {
        // 60 includes: the RFC budget trips at the 11th, so the remaining 49 are
        // never visited. Every query here is preceded by an RFC charge, which is
        // why the binding cap is 10 and the 50-query cap stays a backstop.
        var answers = new Dictionary<string, IReadOnlyList<string>?>
        {
            ["acme.example"] = ["v=spf1 " + string.Join(' ',
                Enumerable.Range(0, 60).Select(i => $"include:f{i}.example.com")) + " -all"],
        };
        foreach (var i in Enumerable.Range(0, 60))
        {
            answers[$"f{i}.example.com"] = ["v=spf1 ip4:198.51.100.1 -all"];
        }

        var txt = new FakeTxt(answers);
        var result = await new SpfDependencyAnalyzer(txt, new FakeMx([]))
            .AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.True(result.OverBudget);
        Assert.Equal(11, result.TotalLookups);
        Assert.Equal(11, txt.Queries); // root + f0..f9; f10 trips the budget before any fetch
        Assert.Equal(50, result.Root.Terms.Count(t => t.Resolution is null && t.CostsLookup));
    }

    [Fact]
    public async Task Analyze_DeepChain_TerminatesAtBudget()
    {
        // 16-deep chain: the 11th include trips the budget, so descent stops at
        // depth 11 with the rest marked unevaluated — depth 10 can only bind
        // walks the budget cannot see, and every level here costs a lookup.
        var answers = new Dictionary<string, IReadOnlyList<string>?>();
        foreach (var i in Enumerable.Range(0, 15))
        {
            answers[$"d{i}.example.com"] = [$"v=spf1 include:d{i + 1}.example.com -all"];
        }

        answers["d15.example.com"] = ["v=spf1 -all"];

        var result = await NewAnalyzer(answers).AnalyzeAsync("d0.example.com", CancellationToken.None);

        Assert.True(result.OverBudget);
        Assert.Equal(11, result.TotalLookups);
        var node = result.Root;
        var depth = 0;
        while (node.Terms.Single(t => t.Kind == SpfTermKind.Include).Resolution is { } next
               && next.Status == SpfNodeStatus.Found)
        {
            node = next;
            depth++;
        }

        Assert.Equal(10, depth); // d1..d10 visited; d11 never fetched
    }

    [Fact]
    public async Task Analyze_UnexpectedResolverThrow_DegradesToFailedRoot()
    {
        var analyzer = new SpfDependencyAnalyzer(new ThrowingTxt(), new FakeMx([]));

        var result = await analyzer.AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(SpfNodeStatus.LookupFailed, result.Root.Status);
        Assert.Contains(result.Root.Issues, i => i.Contains("top-level record above is still accurate"));
    }

    private sealed class ThrowingTxt : IDnsTxtResolver
    {
        public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false)
            => throw new InvalidOperationException("resolver blew up");
    }

    [Fact]
    public async Task Analyze_FiveHundredDomains_StaysBounded()
    {
        // Acceptance shape from #42: adversarial corpus (chains, fans, cycles,
        // voids, failures, macros) across 500 domains must complete with bounded
        // work — no hang, no throw, no unbounded query growth. Fake DNS answers
        // instantly, so this pins the algorithm's bounds, not resolver latency.
        var txt = new Dictionary<string, IReadOnlyList<string>?>();
        var mx = new Dictionary<string, IReadOnlyList<MxHost>?>();

        // Shared leaves every domain fans into — naive re-walking still terminates
        // because each analysis carries its own budget.
        foreach (var j in Enumerable.Range(0, 10))
        {
            txt[$"leaf{j}.example.com"] = ["v=spf1 ip4:198.51.100.1 -all"];
        }

        // Shared 8-deep chain (under the 10 budget, exercises depth handling).
        foreach (var k in Enumerable.Range(0, 8))
        {
            txt[$"chain{k}.example.com"] = [$"v=spf1 include:chain{k + 1}.example.com -all"];
        }

        txt["chain8.example.com"] = ["v=spf1 -all"];

        // Shared two-cycle.
        txt["ring-a.example.com"] = ["v=spf1 include:ring-b.example.com -all"];
        txt["ring-b.example.com"] = ["v=spf1 include:ring-a.example.com -all"];

        var domains = new List<string>();
        foreach (var i in Enumerable.Range(0, 500))
        {
            var domain = $"d{i}.example.com";
            domains.Add(domain);
            txt[domain] = (i % 8) switch
            {
                0 => [$"v=spf1 include:chain0.example.com -all"], // 9-deep shared chain
                1 => ["v=spf1 " + string.Join(' ',
                    Enumerable.Range(0, 10).Select(j => $"include:leaf{j}.example.com")) + " -all"], // 10-wide fan
                2 => ["v=spf1 include:ring-a.example.com -all"], // shared two-cycle
                3 => ["v=spf1 include:%{i}._spf.example.com mx exists:%{i}.example.com -all"], // dynamic
                4 => ["v=spf1 include:ghost-a.example.com include:ghost-b.example.com -all"], // voids
                5 => ["v=spf1 mx:mail.example.com ptr -all"], // mx + deprecated ptr
                6 => ["v=spf1 redirect:leaf0.example.com"], // redirect
                _ => ["v=spf1 ip4:198.51.100.0/24 ip6:2001:db8::/32 -all"], // static, free
            };
        }

        txt["ghost-a.example.com"] = Array.Empty<string>();
        txt["ghost-b.example.com"] = Array.Empty<string>();
        mx["mail.example.com"] = [new MxHost(10, "mx1.example.com"), new MxHost(20, "mx2.example.com")];

        var fakeTxt = new FakeTxt(txt);
        var fakeMx = new FakeMx(mx);
        var analyzer = new SpfDependencyAnalyzer(fakeTxt, fakeMx);

        var started = DateTime.UtcNow;
        var outcomes = new List<string>();
        foreach (var domain in domains)
        {
            var result = await analyzer.AnalyzeAsync(domain, CancellationToken.None);
            outcomes.Add(result.Root.Status);
        }

        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(500, outcomes.Count);
        Assert.DoesNotContain(outcomes, o => o is null);
        // Every analysis is capped at ~12 queries by the RFC budget (each query
        // here is preceded by a charge); 500 × 25 leaves wide headroom while
        // still catching runaway growth (naive shared-subtree re-walking would
        // exceed it only if a cap failed).
        Assert.True(fakeTxt.Queries + fakeMx.Queries <= 500 * 25,
            $"queries: txt={fakeTxt.Queries} mx={fakeMx.Queries}");
        Assert.True(elapsed < TimeSpan.FromSeconds(60), $"elapsed: {elapsed}");
    }

    [Fact]
    public async Task Analyze_TermsAfterAll_AreUnreachableAndUncharged()
    {
        var txt = new FakeTxt(new()
        {
            ["acme.example"] = ["v=spf1 -all include:never.example.com mx"],
            ["never.example.com"] = ["v=spf1 ip4:198.51.100.1 -all"],
        });
        var result = await new SpfDependencyAnalyzer(txt, new FakeMx([]))
            .AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(0, result.TotalLookups);
        Assert.Equal(1, txt.Queries); // root only — nothing after all is visited
        Assert.All(
            result.Root.Terms.Where(t => t.CostsLookup),
            t => Assert.Contains("Unreachable", t.Note, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_MultipleRedirects_FlagsAndFollowsFirst()
    {
        var result = await NewAnalyzer(new()
        {
            ["acme.example"] = ["v=spf1 redirect=first.example.com redirect=second.example.com"],
            ["first.example.com"] = ["v=spf1 ip4:198.51.100.1 -all"],
            ["second.example.com"] = ["v=spf1 ip4:198.51.100.2 -all"],
        }).AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Contains(result.Root.Issues, i => i.Contains("only the first", StringComparison.Ordinal));
        var redirects = result.Root.Terms.Where(t => t.Kind == SpfTermKind.Redirect).ToList();
        Assert.Equal(2, redirects.Count);
        Assert.NotNull(redirects[0].Resolution);
        Assert.Equal("first.example.com", redirects[0].Resolution!.Domain);
        Assert.Null(redirects[1].Resolution);
        Assert.Contains("only the first", redirects[1].Note, StringComparison.Ordinal);
        Assert.Equal(1, result.TotalLookups);
    }

    [Fact]
    public async Task Analyze_ResponseBytes_SumsFetchedPayloads()
    {
        const string root = "v=spf1 include:b.example.com -all";
        const string child = "v=spf1 ip4:198.51.100.1 -all";
        var result = await NewAnalyzer(new()
        {
            ["acme.example"] = [root],
            ["b.example.com"] = [child],
        }).AnalyzeAsync("acme.example", CancellationToken.None);

        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(root) + System.Text.Encoding.UTF8.GetByteCount(child),
            result.EstimatedResponseBytes);
    }
}
