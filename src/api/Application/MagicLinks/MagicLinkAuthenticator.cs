using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.MagicLinks;

public sealed record MagicLinkPrincipal(Guid MagicLinkId, Guid ClientId, string Label);

public interface IMagicLinkAuthenticator
{
    Task<MagicLinkPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct);
    Task TouchLastUsedAsync(Guid magicLinkId, CancellationToken ct);
}

public sealed class MagicLinkAuthenticator(DmarcAnalyzerDbContext db) : IMagicLinkAuthenticator
{
    public const string TokenScheme = "dmarc_ml_v1";

    public async Task<MagicLinkPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct)
    {
        if (!ApiCredentialToken.TryGetPrefix(bearerToken, TokenScheme, out var prefix))
        {
            return null;
        }

        var link = await db.MagicLinks
            .Where(x => x.Prefix == prefix)
            .Select(x => new
            {
                x.Id,
                x.ClientId,
                x.Label,
                x.TokenHash,
                x.ExpiresAtUtc,
                x.RevokedAtUtc,
            })
            .SingleOrDefaultAsync(ct);

        var hashMatches = ApiCredentialToken.HashMatches(bearerToken!, link?.TokenHash);
        if (link is null
            || !hashMatches
            || link.RevokedAtUtc is not null
            || link.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        return new MagicLinkPrincipal(link.Id, link.ClientId, link.Label);
    }

    public async Task TouchLastUsedAsync(Guid magicLinkId, CancellationToken ct)
    {
        var link = await db.MagicLinks.SingleOrDefaultAsync(x => x.Id == magicLinkId, ct);
        if (link is not null)
        {
            link.LastUsedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
}
