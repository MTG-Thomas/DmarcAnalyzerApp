using DmarcAnalyzer.Api.Application.Auth;
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

    public async Task<ReportSourceContext?> AuthenticateAsync(
        Guid sourceId,
        string? bearerToken,
        CancellationToken ct)
    {
        if (!ApiCredentialToken.TryGetPrefix(bearerToken, TokenScheme, out var prefix))
        {
            return null;
        }

        var credential = await db.ApiSourceCredentials
            .AsNoTracking()
            .Where(x => x.ReportSourceId == sourceId && x.Prefix == prefix)
            .Select(x => new
            {
                x.TokenHash,
                x.RevokedAtUtc,
                x.ReportSource!.Protocol,
                x.ReportSource.IsActive,
                x.ReportSource.DefaultClientId,
            })
            .SingleOrDefaultAsync(ct);

        var hashMatches = ApiCredentialToken.HashMatches(bearerToken!, credential?.TokenHash);
        if (credential is null
            || !hashMatches
            || credential.RevokedAtUtc is not null
            || !credential.IsActive
            || !string.Equals(credential.Protocol, "api", StringComparison.Ordinal))
        {
            return null;
        }

        return new ReportSourceContext(
            sourceId,
            credential.DefaultClientId,
            RestrictToDefaultClient: true);
    }
}
