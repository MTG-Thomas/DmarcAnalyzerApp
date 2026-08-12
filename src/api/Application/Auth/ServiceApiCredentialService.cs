using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Auth;

public sealed record ServiceApiCredentialDto(
    Guid Id,
    string Name,
    string Prefix,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc);

public sealed record IssuedServiceApiCredentialDto(
    Guid Id,
    string Name,
    string Prefix,
    string Token,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record CreateServiceApiCredentialRequest(string Name, DateTimeOffset? ExpiresAtUtc);

public interface IServiceApiCredentialService
{
    Task<IReadOnlyList<ServiceApiCredentialDto>> ListAsync(CancellationToken ct);
    Task<ServiceResult<IssuedServiceApiCredentialDto>> IssueAsync(
        CreateServiceApiCredentialRequest request,
        CancellationToken ct);
    Task<ServiceResult<ServiceApiCredentialDto>> RevokeAsync(Guid id, CancellationToken ct);
}

public sealed class ServiceApiCredentialService(DmarcAnalyzerDbContext db) : IServiceApiCredentialService
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(366);

    public async Task<IReadOnlyList<ServiceApiCredentialDto>> ListAsync(CancellationToken ct)
        => await db.ServiceApiCredentials
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => ToDto(x))
            .ToListAsync(ct);

    public async Task<ServiceResult<IssuedServiceApiCredentialDto>> IssueAsync(
        CreateServiceApiCredentialRequest request,
        CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
        {
            return ServiceResult<IssuedServiceApiCredentialDto>.Failure(
                "name must be between 1 and 100 characters and contain no control characters", 400);
        }

        var now = DateTime.UtcNow;
        var expiresAtUtc = request.ExpiresAtUtc?.UtcDateTime ?? now.Add(MaximumLifetime);
        if (expiresAtUtc <= now || expiresAtUtc > now.Add(MaximumLifetime))
        {
            return ServiceResult<IssuedServiceApiCredentialDto>.Failure(
                "expiresAtUtc must be in the future and no more than 366 days away", 400);
        }

        var prefix = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var token = $"dmarc_api_v1.{prefix}.{secret}";
        var credential = new ServiceApiCredential
        {
            Name = name,
            Prefix = prefix,
            TokenHash = SHA256.HashData(Encoding.ASCII.GetBytes(token)),
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
        };

        db.ServiceApiCredentials.Add(credential);
        await db.SaveChangesAsync(ct);

        return ServiceResult<IssuedServiceApiCredentialDto>.Success(new(
            credential.Id,
            credential.Name,
            credential.Prefix,
            token,
            credential.CreatedAtUtc,
            credential.ExpiresAtUtc));
    }

    public async Task<ServiceResult<ServiceApiCredentialDto>> RevokeAsync(
        Guid id,
        CancellationToken ct)
    {
        var credential = await db.ServiceApiCredentials.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (credential is null)
        {
            return ServiceResult<ServiceApiCredentialDto>.Failure("not found", 404);
        }

        if (credential.RevokedAtUtc is null)
        {
            credential.RevokedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return ServiceResult<ServiceApiCredentialDto>.Success(ToDto(credential));
    }

    private static ServiceApiCredentialDto ToDto(ServiceApiCredential credential)
        => new(
            credential.Id,
            credential.Name,
            credential.Prefix,
            credential.CreatedAtUtc,
            credential.ExpiresAtUtc,
            credential.RevokedAtUtc);
}
