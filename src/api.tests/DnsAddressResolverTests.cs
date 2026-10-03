using System.Net;
using System.Reflection;
using DmarcAnalyzer.Api.Application.Analytics;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// <see cref="DnsAddressResolver"/> against canned <see cref="IDnsQuery"/>
/// answers — no live DNS. The proxy below exists because the interface has
/// thirty members and the resolver calls exactly one of them.
/// </summary>
public sealed class DnsAddressResolverTests
{
    public class FakeQueryProxy : DispatchProxy
    {
        public Func<string, QueryType, CancellationToken, Task<IDnsQueryResponse>>? Handler;
        public readonly List<(string Name, QueryType Type)> Calls = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Handler is not null && method?.Name == nameof(IDnsQuery.QueryAsync)
                && args is [string name, QueryType type, QueryClass, CancellationToken ct])
            {
                Calls.Add((name, type));
                return Handler(name, type, ct);
            }

            throw new NotImplementedException($"Unexpected call to {method?.Name}");
        }
    }

    private sealed class FakeResponse(
        IEnumerable<DnsResourceRecord> answers, bool hasError, ushort flags) : IDnsQueryResponse
    {
        public IReadOnlyList<DnsResourceRecord> Answers { get; } = answers.ToList();
        public bool HasError { get; } = hasError;
        public DnsResponseHeader Header { get; } = new DnsResponseHeader(1, flags, 1, 1, 0, 0);
        public string ErrorMessage => string.Empty;
        public string AuditTrail => string.Empty;
        public IReadOnlyList<DnsQuestion> Questions => [];
        public IReadOnlyList<DnsResourceRecord> Additionals => [];
        public IEnumerable<DnsResourceRecord> AllRecords => Answers;
        public IReadOnlyList<DnsResourceRecord> Authorities => [];
        public int MessageSize => 0;
        public NameServer NameServer => null!;
        public DnsQuerySettings Settings => null!;
    }

    private sealed class FakeLocator(Func<string, CancellationToken, Task<IDnsQuery?>> locate)
        : IAuthoritativeDnsClientLocator
    {
        public Task<IDnsQuery?> LocateAsync(string name, CancellationToken ct) => locate(name, ct);
    }

    // Response code is the low 4 bits of the header flags (RFC 1035 §4.1.1).
    private const ushort FlagsNoError = 0x8180;
    private const ushort FlagsServFail = 0x8182;
    private const ushort FlagsNxDomain = 0x8183;

    private static ARecord A(string ip) => new(
        new ResourceRecordInfo("host.example", ResourceRecordType.A, QueryClass.IN, 60, 4),
        IPAddress.Parse(ip));

    private static AaaaRecord Aaaa(string ip) => new(
        new ResourceRecordInfo("host.example", ResourceRecordType.AAAA, QueryClass.IN, 60, 16),
        IPAddress.Parse(ip));

    private static (DnsAddressResolver Resolver, FakeQueryProxy Proxy) NewResolver(
        Func<string, QueryType, CancellationToken, Task<IDnsQueryResponse>> handler,
        Func<string, CancellationToken, Task<IDnsQuery?>>? locate = null)
    {
        var proxy = DispatchProxy.Create<IDnsQuery, FakeQueryProxy>();
        var fake = (FakeQueryProxy)proxy;
        fake.Handler = handler;
        var resolver = new DnsAddressResolver(
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DnsAddressResolver>.Instance,
            new FakeLocator(locate ?? ((_, _) => Task.FromResult<IDnsQuery?>(null))),
            proxy);
        return (resolver, fake);
    }

    [Fact]
    public async Task Success_MapsV4AndV6Separately()
    {
        var (resolver, proxy) = NewResolver((_, type, _) => Task.FromResult<IDnsQueryResponse>(
            type == QueryType.A
                ? new FakeResponse([A("192.0.2.1"), A("192.0.2.2")], false, FlagsNoError)
                : new FakeResponse([Aaaa("2001:db8::1")], false, FlagsNoError)));

        var result = await resolver.ResolveAsync("host.example", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(["192.0.2.1", "192.0.2.2"], result.V4.Select(a => a.ToString()));
        Assert.Equal(["2001:db8::1"], result.V6.Select(a => a.ToString()));
        Assert.Equal([QueryType.A, QueryType.AAAA], proxy.Calls.Select(c => c.Type));
        Assert.All(proxy.Calls, c => Assert.Equal("host.example", c.Name));
    }

    [Fact]
    public async Task SecondCall_ServedFromCacheCaseInsensitively()
    {
        var (resolver, proxy) = NewResolver((_, type, _) => Task.FromResult<IDnsQueryResponse>(
            new FakeResponse(type == QueryType.A ? [A("192.0.2.1")] : [], false, FlagsNoError)));

        var first = await resolver.ResolveAsync("HOST.EXAMPLE", CancellationToken.None);
        var second = await resolver.ResolveAsync("host.example", CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(2, proxy.Calls.Count); // one A + one AAAA, then cache
    }

    [Theory]
    [InlineData(QueryType.A)]
    [InlineData(QueryType.AAAA)]
    public async Task EitherQueryFailing_FailsThePair(QueryType failing)
    {
        var (resolver, _) = NewResolver((_, type, _) => Task.FromResult<IDnsQueryResponse>(
            type == failing
                ? new FakeResponse([], true, FlagsServFail)
                : new FakeResponse(type == QueryType.A ? [A("192.0.2.1")] : [Aaaa("2001:db8::1")],
                    false, FlagsNoError)));

        Assert.Null(await resolver.ResolveAsync("host.example", CancellationToken.None));
    }

    [Fact]
    public async Task Nxdomain_ReadsAsEmptyAnswerNotFailure()
    {
        var (resolver, _) = NewResolver((_, _, _) => Task.FromResult<IDnsQueryResponse>(
            new FakeResponse([], true, FlagsNxDomain)));

        var result = await resolver.ResolveAsync("absent.example", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.V4);
        Assert.Empty(result.V6);
    }

    [Fact]
    public async Task ClientThrowing_ReturnsNull()
    {
        var (resolver, _) = NewResolver((_, _, _) =>
            Task.FromException<IDnsQueryResponse>(new InvalidOperationException("boom")));

        Assert.Null(await resolver.ResolveAsync("host.example", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (resolver, _) = NewResolver((_, _, ct) => Task.FromCanceled<IDnsQueryResponse>(ct));

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => resolver.ResolveAsync("host.example", new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task BypassCache_LocatorNull_FallsBackToNormalPath()
    {
        var (resolver, proxy) = NewResolver((_, type, _) => Task.FromResult<IDnsQueryResponse>(
            new FakeResponse(type == QueryType.A ? [A("192.0.2.9")] : [], false, FlagsNoError)));

        var result = await resolver.ResolveAsync("host.example", CancellationToken.None, bypassCache: true);

        Assert.NotNull(result);
        Assert.Equal(["192.0.2.9"], result.V4.Select(a => a.ToString()));
        Assert.Equal(2, proxy.Calls.Count);
    }

    [Fact]
    public async Task BypassCache_LocatorThrowing_FallsBackToNormalPath()
    {
        var (resolver, proxy) = NewResolver(
            (_, type, _) => Task.FromResult<IDnsQueryResponse>(
                new FakeResponse(type == QueryType.A ? [A("192.0.2.9")] : [], false, FlagsNoError)),
            (_, _) => Task.FromException<IDnsQuery?>(new InvalidOperationException("no NS")));

        var result = await resolver.ResolveAsync("host.example", CancellationToken.None, bypassCache: true);

        Assert.NotNull(result);
        Assert.Equal(2, proxy.Calls.Count);
    }

    [Fact]
    public async Task BypassCache_AuthoritativeSuccess_IsCached()
    {
        var authority = DispatchProxy.Create<IDnsQuery, FakeQueryProxy>();
        var authorityFake = (FakeQueryProxy)authority;
        authorityFake.Handler = (_, type, _) => Task.FromResult<IDnsQueryResponse>(
            new FakeResponse(type == QueryType.A ? [A("192.0.2.7")] : [Aaaa("2001:db8::7")],
                false, FlagsNoError));
        var (resolver, proxy) = NewResolver(
            (_, _, _) => throw new InvalidOperationException("normal path must not run"),
            (_, _) => Task.FromResult<IDnsQuery?>(authority));

        var result = await resolver.ResolveAsync("host.example", CancellationToken.None, bypassCache: true);
        var cached = await resolver.ResolveAsync("host.example", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(["192.0.2.7"], result.V4.Select(a => a.ToString()));
        Assert.Equal(["2001:db8::7"], result.V6.Select(a => a.ToString()));
        Assert.Same(result, cached);
        Assert.Empty(proxy.Calls);
        Assert.Equal(2, authorityFake.Calls.Count);
    }

    [Fact]
    public async Task BypassCache_AuthoritativeServfail_FallsBackToNormalPath()
    {
        var authority = DispatchProxy.Create<IDnsQuery, FakeQueryProxy>();
        ((FakeQueryProxy)authority).Handler = (_, _, _) => Task.FromResult<IDnsQueryResponse>(
            new FakeResponse([], true, FlagsServFail));
        var (resolver, proxy) = NewResolver(
            (_, type, _) => Task.FromResult<IDnsQueryResponse>(
                new FakeResponse(type == QueryType.A ? [A("192.0.2.9")] : [], false, FlagsNoError)),
            (_, _) => Task.FromResult<IDnsQuery?>(authority));

        var result = await resolver.ResolveAsync("host.example", CancellationToken.None, bypassCache: true);

        Assert.NotNull(result);
        Assert.Equal(["192.0.2.9"], result.V4.Select(a => a.ToString()));
        Assert.Equal(2, proxy.Calls.Count);
    }
}
