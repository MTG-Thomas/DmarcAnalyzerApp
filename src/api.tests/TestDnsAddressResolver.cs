using System.Net;
using DmarcAnalyzer.Api.Application.Analytics;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Stubs live DNS for address lookups: A/AAAA answers per name, null for a
/// failed lookup, empty for NXDOMAIN — mirroring <see cref="TestDnsTxtResolver"/>.
/// </summary>
public sealed class TestDnsAddressResolver : IDnsAddressResolver
{
    private readonly Dictionary<string, DnsAddresses?> _byName = new(StringComparer.OrdinalIgnoreCase);

    public TestDnsAddressResolver Publish(string name, params string[] ips)
    {
        var parsed = ips.Select(IPAddress.Parse).ToList();
        _byName[name] = new DnsAddresses(
            parsed.Where(p => p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList(),
            parsed.Where(p => p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).ToList());
        return this;
    }

    /// <summary>Simulates a timeout/servfail — unknown, not missing.</summary>
    public TestDnsAddressResolver FailFor(string name)
    {
        _byName[name] = null;
        return this;
    }

    public Task<DnsAddresses?> ResolveAsync(string domain, CancellationToken ct, bool bypassCache = false)
        => Task.FromResult(_byName.TryGetValue(domain, out var addresses)
            ? addresses
            : new DnsAddresses([], []));
}
