using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.MailboxSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.MailboxSources;

public sealed class MailboxSourceService(DmarcAnalyzerDbContext db, ICredentialProtector credentialProtector) : IMailboxSourceService
{
    private static readonly string[] SupportedProtocols = ["imap", "pop3", "api"];

    public async Task<IReadOnlyList<MailboxSourceDto>> ListAsync(CancellationToken ct)
    {
        return await db.MailboxSources
            .AsNoTracking()
            .Include(x => x.DefaultClient)
            .OrderBy(x => x.Name)
            .Select(x => ToDto(x, x.DefaultClient != null ? x.DefaultClient.Name : null))
            .ToListAsync(ct);
    }

    public async Task<ServiceResult<MailboxSourceDto>> CreateAsync(CreateMailboxSourceRequest request, CancellationToken ct)
    {
        var protocol = request.Protocol?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SupportedProtocols.Contains(protocol))
        {
            return ServiceResult<MailboxSourceDto>.Failure("protocol must be imap, pop3, or api", 400);
        }

        if (string.IsNullOrWhiteSpace(request.Name) ||
            request.DefaultClientId == Guid.Empty)
        {
            return ServiceResult<MailboxSourceDto>.Failure("name and defaultClientId are required", 400);
        }

        var isApi = protocol == "api";
        if (!isApi && !HasCompleteMailboxConfiguration(
                request.Host, request.Port, request.UseTls, request.Username, request.Password))
        {
            return ServiceResult<MailboxSourceDto>.Failure(
                "host, port, useTls, username, and password are required for mailbox sources", 400);
        }

        if (isApi && request.DeleteAfterRetention)
        {
            return ServiceResult<MailboxSourceDto>.Failure(
                "mailbox retention cannot be enabled for an API source", 400);
        }

        var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId, ct);
        if (!clientExists)
        {
            return ServiceResult<MailboxSourceDto>.Failure("default client not found", 400);
        }

        var now = DateTime.UtcNow;
        var source = new MailboxSource
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

        db.MailboxSources.Add(source);
        await db.SaveChangesAsync(ct);

        return ServiceResult<MailboxSourceDto>.Success(ToDto(source, null));
    }

    public async Task<ServiceResult<MailboxSourceDto>> UpdateAsync(Guid id, UpdateMailboxSourceRequest request, CancellationToken ct)
    {
        var source = await db.MailboxSources.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (source is null)
        {
            return ServiceResult<MailboxSourceDto>.Failure("not found", 404);
        }

        var protocol = source.Protocol;
        if (request.Protocol is not null)
        {
            protocol = request.Protocol.Trim().ToLowerInvariant();
            if (!SupportedProtocols.Contains(protocol))
            {
                return ServiceResult<MailboxSourceDto>.Failure("protocol must be imap, pop3, or api", 400);
            }
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ServiceResult<MailboxSourceDto>.Failure("name cannot be empty", 400);
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
                return ServiceResult<MailboxSourceDto>.Failure("host cannot be empty", 400);
            }

            host = request.Host.Trim().ToLowerInvariant();
        }

        if (protocol != "api" && request.Port.HasValue)
        {
            if (request.Port.Value <= 0)
            {
                return ServiceResult<MailboxSourceDto>.Failure("port must be greater than 0", 400);
            }

            port = request.Port.Value;
        }

        if (protocol != "api" && request.Username is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return ServiceResult<MailboxSourceDto>.Failure("username cannot be empty", 400);
            }

            username = request.Username.Trim();
        }

        if (protocol != "api" && request.Password is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return ServiceResult<MailboxSourceDto>.Failure("password cannot be empty", 400);
            }

            passwordEncrypted = credentialProtector.Protect(request.Password);
        }

        if (request.DefaultClientId.HasValue)
        {
            if (request.DefaultClientId.Value == Guid.Empty)
            {
                return ServiceResult<MailboxSourceDto>.Failure("defaultClientId cannot be empty", 400);
            }

            var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId.Value, ct);
            if (!clientExists)
            {
                return ServiceResult<MailboxSourceDto>.Failure("default client not found", 400);
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
                return ServiceResult<MailboxSourceDto>.Failure(
                    "mailbox retention cannot be enabled for an API source", 400);
            }

            host = null;
            port = null;
            useTls = null;
            username = null;
            passwordEncrypted = null;
            deleteAfterRetention = false;
            source.OldestMessageAtUtc = null;
            source.LastSuccessSyncAtUtc = null;
            source.LastProcessedUid = null;
            source.LastProcessedUidValidity = null;
        }
        else if (!HasCompleteMailboxConfiguration(host, port, useTls, username, passwordEncrypted))
        {
            return ServiceResult<MailboxSourceDto>.Failure(
                "host, port, useTls, username, and password are required when changing an API source to a mailbox source", 400);
        }

        source.Protocol = protocol;
        source.Host = host;
        source.Port = port;
        source.UseTls = useTls;
        source.Username = username;
        source.PasswordEncrypted = passwordEncrypted;
        source.DeleteAfterRetention = deleteAfterRetention;
        source.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<MailboxSourceDto>.Success(ToDto(source, null));
    }

    private static MailboxSourceDto ToDto(MailboxSource x, string? defaultClientName) =>
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
}
