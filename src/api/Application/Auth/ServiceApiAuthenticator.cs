using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Auth;

public sealed record ServiceApiPrincipal(Guid CredentialId, string Name);

public interface IServiceApiAuthenticator
{
    Task<ServiceApiPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct);
}

public sealed class ServiceApiAuthenticator(DmarcAnalyzerDbContext db) : IServiceApiAuthenticator
{
    private const string TokenScheme = "dmarc_api_v1";

    public async Task<ServiceApiPrincipal?> AuthenticateAsync(
        string? bearerToken,
        CancellationToken ct)
    {
        if (!ApiCredentialToken.TryGetPrefix(bearerToken, TokenScheme, out var prefix))
        {
            return null;
        }

        var credential = await db.ServiceApiCredentials
            .AsNoTracking()
            .Where(x => x.Prefix == prefix)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.TokenHash,
                x.ExpiresAtUtc,
                x.RevokedAtUtc,
            })
            .SingleOrDefaultAsync(ct);

        var hashMatches = ApiCredentialToken.HashMatches(bearerToken!, credential?.TokenHash);
        if (credential is null
            || !hashMatches
            || credential.RevokedAtUtc is not null
            || credential.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        return new ServiceApiPrincipal(credential.Id, credential.Name);
    }
}
