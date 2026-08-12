using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly byte[] MissingCredentialHash = new byte[32];

    public async Task<ServiceApiPrincipal?> AuthenticateAsync(
        string? bearerToken,
        CancellationToken ct)
    {
        if (!TryGetPrefix(bearerToken, out var prefix))
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

        var candidateHash = SHA256.HashData(Encoding.ASCII.GetBytes(bearerToken!));
        var hashMatches = CryptographicOperations.FixedTimeEquals(
            candidateHash,
            credential?.TokenHash ?? MissingCredentialHash);

        if (credential is null
            || !hashMatches
            || credential.RevokedAtUtc is not null
            || credential.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        return new ServiceApiPrincipal(credential.Id, credential.Name);
    }

    private static bool TryGetPrefix(string? token, out string prefix)
    {
        prefix = string.Empty;
        if (token is null)
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3
            || !string.Equals(parts[0], TokenScheme, StringComparison.Ordinal)
            || parts[1].Length != 22
            || parts[2].Length != 43
            || !Base64Url.IsValid(parts[1])
            || !Base64Url.IsValid(parts[2]))
        {
            return false;
        }

        prefix = parts[1];
        return true;
    }
}
