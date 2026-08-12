using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.ReportSources;

public sealed class ReportSourceService(
    DmarcAnalyzerDbContext db,
    ICredentialProtector credentialProtector,
    ICurrentUserContext currentUser) : IReportSourceService
{
    private static readonly string[] SupportedProtocols = ["imap", "api"];

    public async Task<IReadOnlyList<ReportSourceDto>> ListAsync(CancellationToken ct)
    {
        return await db.ReportSources
            .AsNoTracking()
            .Include(x => x.DefaultClient)
            .OrderBy(x => x.Name)
            .Select(x => ToDto(x, x.DefaultClient != null ? x.DefaultClient.Name : null))
            .ToListAsync(ct);
    }

    public async Task<ServiceResult<ReportSourceDto>> CreateAsync(CreateReportSourceRequest request, CancellationToken ct)
    {
        var protocol = request.Protocol?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SupportedProtocols.Contains(protocol))
        {
            return ServiceResult<ReportSourceDto>.Failure("protocol must be imap or api", 400);
        }

        if (currentUser.IsService && protocol != "api")
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "service credentials may create API report sources only", 403);
        }

        if (string.IsNullOrWhiteSpace(request.Name) ||
            request.DefaultClientId == Guid.Empty)
        {
            return ServiceResult<ReportSourceDto>.Failure("name and defaultClientId are required", 400);
        }

        var isApi = protocol == "api";
        if (!isApi && !HasCompleteMailboxConfiguration(
                request.Host, request.Port, request.UseTls, request.Username, request.Password))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "host, port, useTls, username, and password are required for mailbox sources", 400);
        }

        if (isApi && request.DeleteAfterRetention)
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "mailbox retention cannot be enabled for an API source", 400);
        }

        var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId, ct);
        if (!clientExists)
        {
            return ServiceResult<ReportSourceDto>.Failure("default client not found", 400);
        }

        var now = DateTime.UtcNow;
        var source = new ReportSource
        {
            Name = request.Name.Trim(),
            Protocol = protocol,
            Host = isApi ? null : request.Host!.Trim().ToLowerInvariant(),
            Port = isApi ? null : request.Port,
            UseTls = isApi ? null : request.UseTls,
            Username = isApi ? null : request.Username!.Trim(),
            PasswordEncrypted = isApi ? null : credentialProtector.Protect(request.Password!),
            DefaultClientId = request.DefaultClientId,
            IsActive = request.IsActive,
            DeleteAfterRetention = !isApi && request.DeleteAfterRetention,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        source.NormalizeProtocolState();

        db.ReportSources.Add(source);
        await db.SaveChangesAsync(ct);

        return ServiceResult<ReportSourceDto>.Success(ToDto(source, null));
    }

    public async Task<ServiceResult<ReportSourceDto>> UpdateAsync(Guid id, UpdateReportSourceRequest request, CancellationToken ct)
    {
        var source = await db.ReportSources.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (source is null)
        {
            return ServiceResult<ReportSourceDto>.Failure("not found", 404);
        }

        if (currentUser.IsService && !IsServiceSafeApiUpdate(source, request))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "service credentials may update API report-source metadata only", 403);
        }

        var protocol = source.Protocol;
        if (request.Protocol is not null)
        {
            protocol = request.Protocol.Trim().ToLowerInvariant();
            var unchanged = string.Equals(protocol, source.Protocol, StringComparison.Ordinal);
            if (!unchanged && !SupportedProtocols.Contains(protocol))
            {
                return ServiceResult<ReportSourceDto>.Failure("protocol must be imap or api", 400);
            }
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ServiceResult<ReportSourceDto>.Failure("name cannot be empty", 400);
            }

            source.Name = request.Name.Trim();
        }

        var host = source.Host;
        var port = source.Port;
        var useTls = source.UseTls;
        var username = source.Username;
        var passwordEncrypted = source.PasswordEncrypted;

        if (protocol != "api" && request.Host is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Host))
            {
                return ServiceResult<ReportSourceDto>.Failure("host cannot be empty", 400);
            }

            host = request.Host.Trim().ToLowerInvariant();
        }

        if (protocol != "api" && request.Port.HasValue)
        {
            if (request.Port.Value <= 0)
            {
                return ServiceResult<ReportSourceDto>.Failure("port must be greater than 0", 400);
            }

            port = request.Port.Value;
        }

        if (protocol != "api" && request.Username is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return ServiceResult<ReportSourceDto>.Failure("username cannot be empty", 400);
            }

            username = request.Username.Trim();
        }

        if (protocol != "api" && request.Password is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return ServiceResult<ReportSourceDto>.Failure("password cannot be empty", 400);
            }

            passwordEncrypted = credentialProtector.Protect(request.Password);
        }

        if (request.DefaultClientId.HasValue)
        {
            if (request.DefaultClientId.Value == Guid.Empty)
            {
                return ServiceResult<ReportSourceDto>.Failure("defaultClientId cannot be empty", 400);
            }

            var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId.Value, ct);
            if (!clientExists)
            {
                return ServiceResult<ReportSourceDto>.Failure("default client not found", 400);
            }

            source.DefaultClientId = request.DefaultClientId.Value;
        }

        if (protocol != "api" && request.UseTls.HasValue)
        {
            useTls = request.UseTls.Value;
        }

        if (request.IsActive.HasValue)
        {
            source.IsActive = request.IsActive.Value;
        }

        var deleteAfterRetention = request.DeleteAfterRetention ?? source.DeleteAfterRetention;
        if (protocol == "api")
        {
            if (request.DeleteAfterRetention == true)
            {
                return ServiceResult<ReportSourceDto>.Failure(
                    "mailbox retention cannot be enabled for an API source", 400);
            }

        }
        else if (!HasCompleteMailboxConfiguration(host, port, useTls, username, passwordEncrypted))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "host, port, useTls, username, and password are required when changing an API source to a mailbox source", 400);
        }

        if (source.Protocol == "api" && protocol != "api")
        {
            await ApiSourceCredentialLifecycle.RevokeActiveAsync(db, source.Id, ct);
        }

        source.Protocol = protocol;
        source.Host = host;
        source.Port = port;
        source.UseTls = useTls;
        source.Username = username;
        source.PasswordEncrypted = passwordEncrypted;
        source.DeleteAfterRetention = deleteAfterRetention;
        source.NormalizeProtocolState();
        source.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<ReportSourceDto>.Success(ToDto(source, null));
    }

    private static ReportSourceDto ToDto(ReportSource x, string? defaultClientName) =>
        new(
            x.Id,
            x.Name,
            x.Protocol,
            x.Host,
            x.Port,
            x.UseTls,
            x.Username,
            x.DefaultClientId,
            defaultClientName,
            x.IsActive,
            x.DeleteAfterRetention,
            x.OldestMessageAtUtc,
            x.LastSuccessSyncAtUtc,
            x.LastProcessedUid,
            x.LastProcessedUidValidity,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);

    private static bool HasCompleteMailboxConfiguration(
        string? host,
        int? port,
        bool? useTls,
        string? username,
        string? password)
        => !string.IsNullOrWhiteSpace(host)
           && port is > 0
           && useTls.HasValue
           && !string.IsNullOrWhiteSpace(username)
           && !string.IsNullOrWhiteSpace(password);

    private static bool IsServiceSafeApiUpdate(ReportSource source, UpdateReportSourceRequest request)
        => source.Protocol == "api"
           && (request.Protocol is null || request.Protocol.Trim().Equals("api", StringComparison.OrdinalIgnoreCase))
           && request.Host is null
           && request.Port is null
           && request.UseTls is null
           && request.Username is null
           && request.Password is null
           && request.DeleteAfterRetention is null;
}
