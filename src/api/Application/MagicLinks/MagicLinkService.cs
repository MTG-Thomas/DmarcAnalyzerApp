using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.MagicLinks;

public sealed record MagicLinkDto(
    Guid Id,
    Guid ClientId,
    string? ClientName,
    string Label,
    string Prefix,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    DateTime? LastUsedAtUtc);

public sealed record IssuedMagicLinkDto(
    Guid Id,
    Guid ClientId,
    string Label,
    string Prefix,
    string Token,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record CreateMagicLinkRequest(
    Guid ClientId,
    int? ExpiresInDays,
    string? Label);

public interface IMagicLinkService
{
    Task<IReadOnlyList<MagicLinkDto>> ListAsync(Guid? clientId, CancellationToken ct);
    Task<ServiceResult<IssuedMagicLinkDto>> IssueAsync(CreateMagicLinkRequest request, CancellationToken ct);
    Task<ServiceResult<MagicLinkDto>> RevokeAsync(Guid id, CancellationToken ct);
}

public sealed class MagicLinkService(DmarcAnalyzerDbContext db) : IMagicLinkService
{
    public const int DefaultExpiryDays = 7;
    public const int MaxExpiryDays = 30;

    public async Task<IReadOnlyList<MagicLinkDto>> ListAsync(Guid? clientId, CancellationToken ct)
    {
        var query = db.MagicLinks.AsNoTracking().AsQueryable();
        if (clientId.HasValue)
        {
            query = query.Where(x => x.ClientId == clientId.Value);
        }

        return await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new MagicLinkDto(
                x.Id,
                x.ClientId,
                x.Client != null ? x.Client.Name : null,
                x.Label,
                x.Prefix,
                x.CreatedAtUtc,
                x.ExpiresAtUtc,
                x.RevokedAtUtc,
                x.LastUsedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<ServiceResult<IssuedMagicLinkDto>> IssueAsync(
        CreateMagicLinkRequest request,
        CancellationToken ct)
    {
        var clientExists = await db.Clients.AnyAsync(x => x.Id == request.ClientId, ct);
        if (!clientExists)
        {
            return ServiceResult<IssuedMagicLinkDto>.Failure("client not found", 404);
        }

        var label = request.Label?.Trim() ?? string.Empty;
        if (label.Length is < 1 or > 100 || label.Any(char.IsControl))
        {
            return ServiceResult<IssuedMagicLinkDto>.Failure(
                "label must be between 1 and 100 characters and contain no control characters", 400);
        }

        var expiresInDays = request.ExpiresInDays ?? DefaultExpiryDays;
        if (expiresInDays is < 1 or > MaxExpiryDays)
        {
            return ServiceResult<IssuedMagicLinkDto>.Failure(
                $"expiresInDays must be between 1 and {MaxExpiryDays}", 400);
        }

        var now = DateTime.UtcNow;
        var minted = ApiCredentialToken.Mint(MagicLinkAuthenticator.TokenScheme);
        var link = new MagicLink
        {
            ClientId = request.ClientId,
            Label = label,
            Prefix = minted.Prefix,
            TokenHash = minted.TokenHash,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(expiresInDays),
        };

        db.MagicLinks.Add(link);
        await db.SaveChangesAsync(ct);

        return ServiceResult<IssuedMagicLinkDto>.Success(new(
            link.Id,
            link.ClientId,
            link.Label,
            link.Prefix,
            minted.Token,
            link.CreatedAtUtc,
            link.ExpiresAtUtc));
    }

    public async Task<ServiceResult<MagicLinkDto>> RevokeAsync(Guid id, CancellationToken ct)
    {
        var link = await db.MagicLinks
            .Include(x => x.Client)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (link is null)
        {
            return ServiceResult<MagicLinkDto>.Failure("not found", 404);
        }

        if (link.RevokedAtUtc is null)
        {
            link.RevokedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return ServiceResult<MagicLinkDto>.Success(ToDto(link));
    }

    private static MagicLinkDto ToDto(MagicLink link)
        => new(
            link.Id,
            link.ClientId,
            link.Client?.Name,
            link.Label,
            link.Prefix,
            link.CreatedAtUtc,
            link.ExpiresAtUtc,
            link.RevokedAtUtc,
            link.LastUsedAtUtc);
}
