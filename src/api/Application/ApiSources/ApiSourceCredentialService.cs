using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DmarcAnalyzer.Api.Application.ApiSources;

public sealed record ApiSourceCredentialDto(
    Guid Id,
    Guid SourceId,
    string Prefix,
    DateTime CreatedAtUtc,
    DateTime? RevokedAtUtc);

public sealed record IssuedApiSourceCredentialDto(
    Guid Id,
    Guid SourceId,
    string Prefix,
    string Token,
    DateTime CreatedAtUtc);

public interface IApiSourceCredentialService
{
    Task<ServiceResult<IReadOnlyList<ApiSourceCredentialDto>>> ListAsync(Guid sourceId, CancellationToken ct);
    Task<ServiceResult<IssuedApiSourceCredentialDto>> IssueAsync(Guid sourceId, CancellationToken ct);
    Task<ServiceResult<ApiSourceCredentialDto>> RevokeAsync(Guid sourceId, Guid credentialId, CancellationToken ct);
}

public sealed class ApiSourceCredentialService(DmarcAnalyzerDbContext db) : IApiSourceCredentialService
{
    public async Task<ServiceResult<IReadOnlyList<ApiSourceCredentialDto>>> ListAsync(
        Guid sourceId,
        CancellationToken ct)
    {
        if (!await db.MailboxSources.AnyAsync(x => x.Id == sourceId, ct))
        {
            return ServiceResult<IReadOnlyList<ApiSourceCredentialDto>>.Failure("not found", 404);
        }

        var credentials = await db.ApiSourceCredentials
            .AsNoTracking()
            .Where(x => x.MailboxSourceId == sourceId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => ToDto(x))
            .ToListAsync(ct);

        return ServiceResult<IReadOnlyList<ApiSourceCredentialDto>>.Success(credentials);
    }

    public async Task<ServiceResult<IssuedApiSourceCredentialDto>> IssueAsync(
        Guid sourceId,
        CancellationToken ct)
    {
        var source = await db.MailboxSources
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sourceId, ct);

        if (source is null)
        {
            return ServiceResult<IssuedApiSourceCredentialDto>.Failure("not found", 404);
        }

        if (!string.Equals(source.Protocol, "api", StringComparison.Ordinal))
        {
            return ServiceResult<IssuedApiSourceCredentialDto>.Failure(
                "credentials can only be issued for API sources", 400);
        }

        var prefix = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var token = $"dmarc_v1.{prefix}.{secret}";
        var now = DateTime.UtcNow;
        var credential = new ApiSourceCredential
        {
            MailboxSourceId = sourceId,
            Prefix = prefix,
            TokenHash = SHA256.HashData(Encoding.ASCII.GetBytes(token)),
            CreatedAtUtc = now,
        };

        db.ApiSourceCredentials.Add(credential);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.CheckViolation,
            ConstraintName: "CK_api_source_credential_SourceProtocol",
        })
        {
            db.Entry(credential).State = EntityState.Detached;
            return ServiceResult<IssuedApiSourceCredentialDto>.Failure(
                "credentials can only be issued for API sources", 400);
        }

        return ServiceResult<IssuedApiSourceCredentialDto>.Success(new(
            credential.Id,
            credential.MailboxSourceId,
            credential.Prefix,
            token,
            credential.CreatedAtUtc));
    }

    public async Task<ServiceResult<ApiSourceCredentialDto>> RevokeAsync(
        Guid sourceId,
        Guid credentialId,
        CancellationToken ct)
    {
        var credential = await db.ApiSourceCredentials.SingleOrDefaultAsync(
            x => x.Id == credentialId && x.MailboxSourceId == sourceId,
            ct);

        if (credential is null)
        {
            return ServiceResult<ApiSourceCredentialDto>.Failure("not found", 404);
        }

        if (credential.RevokedAtUtc is null)
        {
            credential.RevokedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return ServiceResult<ApiSourceCredentialDto>.Success(ToDto(credential));
    }

    private static ApiSourceCredentialDto ToDto(ApiSourceCredential credential)
        => new(
            credential.Id,
            credential.MailboxSourceId,
            credential.Prefix,
            credential.CreatedAtUtc,
            credential.RevokedAtUtc);
}

public static class ApiSourceCredentialLifecycle
{
    public static async Task RevokeActiveAsync(
        DmarcAnalyzerDbContext db,
        Guid sourceId,
        CancellationToken ct)
    {
        var activeCredentials = await db.ApiSourceCredentials
            .Where(x => x.MailboxSourceId == sourceId && x.RevokedAtUtc == null)
            .ToListAsync(ct);
        var revokedAtUtc = DateTime.UtcNow;
        foreach (var credential in activeCredentials)
        {
            credential.RevokedAtUtc = revokedAtUtc;
        }
    }
}
