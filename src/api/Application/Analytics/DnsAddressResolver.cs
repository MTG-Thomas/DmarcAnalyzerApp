using System.Net;
using DnsClient;
using Microsoft.Extensions.Caching.Memory;

namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>A and AAAA addresses for one name, v4 and v6 kept separate for SPF term building.</summary>
public sealed record DnsAddresses(IReadOnlyList<IPAddress> V4, IReadOnlyList<IPAddress> V6);

/// <summary>Address lookups with a short cache — see <see cref="DnsAddressResolver"/>.</summary>
public interface IDnsAddressResolver
{
    /// <summary>
    /// A and AAAA addresses published for <paramref name="domain"/>.
    /// Returns null when the lookup itself failed (timeout/servfail) — distinct
    /// from an empty answer, which means NXDOMAIN or no address records.
    /// </summary>
    /// <param name="bypassCache">See <see cref="IDnsTxtResolver.ResolveAsync"/> — same rationale.</param>
    Task<DnsAddresses?> ResolveAsync(string domain, CancellationToken ct, bool bypassCache = false);
}

/// <summary>
/// A/AAAA lookups against the host's configured resolver, mirroring
/// <see cref="DnsTxtResolver"/> — same privacy rationale (no third-party DoH),
/// same short cache so a page refresh doesn't re-query DNS.
/// </summary>
public sealed class DnsAddressResolver(
    IMemoryCache cache,
    ILogger<DnsAddressResolver> logger,
    IAuthoritativeDnsClientLocator authoritativeLocator,
    IDnsQuery? queryClient = null) : IDnsAddressResolver
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly LookupClient Client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
        UseCache = false, // IMemoryCache above is the cache; keep layers single-purpose
    });

    // Injectable only so tests can substitute canned answers — production
    // always takes the default. LookupClient itself has no testing seam.
    private readonly IDnsQuery _query = queryClient ?? Client;

    /// <inheritdoc />
    public async Task<DnsAddresses?> ResolveAsync(string domain, CancellationToken ct, bool bypassCache = false)
    {
        var key = $"dns-addr:{domain.ToLowerInvariant()}";
        if (!bypassCache && cache.TryGetValue<DnsAddresses>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        // See DnsTxtResolver.ResolveAsync — same "the host's own resolver may
        // still be stale" rationale for going straight to the authoritative
        // server on an explicit bypass, with fallback to the normal path.
        if (bypassCache)
        {
            var authoritative = await TryResolveAuthoritativeAsync(domain, ct);
            if (authoritative is not null)
            {
                cache.Set(key, authoritative, SuccessTtl);
                return authoritative;
            }
        }

        try
        {
            var v4Response = await _query.QueryAsync(domain, QueryType.A, cancellationToken: ct);
            var v6Response = await _query.QueryAsync(domain, QueryType.AAAA, cancellationToken: ct);

            // Same contract as the TXT resolver: SERVFAIL/REFUSED reads as
            // "couldn't check" (null), NXDOMAIN is a definitive empty answer.
            // Either query failing fails the pair — partial address sets would
            // silently drop mail paths from a flattened candidate.
            if (Failed(v4Response) || Failed(v6Response))
            {
                logger.LogWarning("Address lookup for {Domain} failed", domain);
                return null;
            }

            var result = new DnsAddresses(
                v4Response.Answers.ARecords().Select(r => r.Address).ToArray(),
                v6Response.Answers.AaaaRecords().Select(r => r.Address).ToArray());
            cache.Set(key, result, SuccessTtl);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Address lookup failed for {Domain}", domain);
            return null; // lookup failure — caller reports "couldn't check", not "missing"
        }
    }

    private static bool Failed(IDnsQueryResponse response)
        => response.HasError && response.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain;

    private async Task<DnsAddresses?> TryResolveAuthoritativeAsync(string domain, CancellationToken ct)
    {
        try
        {
            var client = await authoritativeLocator.LocateAsync(domain, ct);
            if (client is null)
            {
                return null;
            }

            var v4Response = await client.QueryAsync(domain, QueryType.A, cancellationToken: ct);
            var v6Response = await client.QueryAsync(domain, QueryType.AAAA, cancellationToken: ct);
            if (Failed(v4Response) || Failed(v6Response))
            {
                return null;
            }

            return new DnsAddresses(
                v4Response.Answers.ARecords().Select(r => r.Address).ToArray(),
                v6Response.Answers.AaaaRecords().Select(r => r.Address).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Authoritative address lookup failed for {Domain}, falling back", domain);
            return null;
        }
    }
}
