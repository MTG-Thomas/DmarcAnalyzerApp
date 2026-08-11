using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.ApiSources;

public interface IApiSourceAuthenticator
{
    Task<ReportSourceContext?> AuthenticateAsync(
        Guid sourceId,
        string? bearerToken,
        CancellationToken ct);
}

public sealed class ApiSourceAuthenticator(DmarcAnalyzerDbContext db) : IApiSourceAuthenticator
{
    private const string TokenScheme = "dmarc_v1";
    private static readonly byte[] MissingCredentialHash = new byte[32];

    public async Task<ReportSourceContext?> AuthenticateAsync(
        Guid sourceId,
        string? bearerToken,
        CancellationToken ct)
    {
        if (!TryGetPrefix(bearerToken, out var prefix))
        {
            return null;
        }

        var credential = await db.ApiSourceCredentials
            .AsNoTracking()
            .Where(x => x.MailboxSourceId == sourceId && x.Prefix == prefix)
            .Select(x => new
            {
                x.TokenHash,
                x.RevokedAtUtc,
                x.MailboxSource!.Protocol,
                x.MailboxSource.IsActive,
                x.MailboxSource.DefaultClientId,
            })
            .SingleOrDefaultAsync(ct);

        var candidateHash = SHA256.HashData(Encoding.ASCII.GetBytes(bearerToken!));
        var hashMatches = CryptographicOperations.FixedTimeEquals(
            candidateHash,
            credential?.TokenHash ?? MissingCredentialHash);

        if (credential is null
            || !hashMatches
            || credential.RevokedAtUtc is not null
            || !credential.IsActive
            || !string.Equals(credential.Protocol, "api", StringComparison.Ordinal))
        {
            return null;
        }

        return new ReportSourceContext(sourceId, credential.DefaultClientId);
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
