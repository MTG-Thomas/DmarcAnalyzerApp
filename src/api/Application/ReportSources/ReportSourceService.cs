using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.ReportSources;

/// <summary>EF-backed <see cref="IReportSourceService"/>.</summary>
public sealed class ReportSourceService(
    DmarcAnalyzerDbContext db,
    ICredentialProtector credentialProtector,
    ICurrentUserContext currentUser) : IReportSourceService
{
    /// <summary>
    /// What a source may actually be, not what it might one day be. Every value here is
    /// read by something: <c>imap</c>, <c>pop3</c> and <c>s3</c> by the polling worker,
    /// <c>api</c> by the ingestion endpoint.
    /// <para>
    /// <c>pop3</c> is here on its second attempt. It was accepted for a long time and never
    /// worked — the worker polled <c>Protocol == "imap"</c> and manual sync refused anything
    /// else, so a POP3 source could be created, would appear in the console, and would
    /// silently never ingest a single report — and was removed on the principle that an
    /// option that does nothing is worse than an absent one. It is back because the code
    /// that reads it now exists: <c>Pop3MailboxTransport</c>, the same drain, the same run
    /// rows and the same retention deletion as IMAP. Rows predating the removal start
    /// syncing on the next pass, which is what they were always meant to do.
    /// </para>
    /// <para>
    /// The rule the round trip is worth remembering for: add a value here in the same change
    /// as the code that acts on it, never before.
    /// </para>
    /// </summary>
    private static readonly string[] SupportedProtocols =
    [
        ReportSourceProtocols.Imap,
        ReportSourceProtocols.Pop3,
        ReportSourceProtocols.S3,
        ReportSourceProtocols.Api,
    ];

    private const string ProtocolError = "protocol must be imap, pop3, s3 or api";

    /// <summary>
    /// An API source is pushed to, so it carries no mailbox connection and no credential.
    /// Those columns stay nullable and hold null rather than sentinel values — every
    /// reader treats null as absent, and <c>NormalizeProtocolState</c> enforces it.
    /// </summary>
    private static bool IsPushed(string protocol) => protocol == ReportSourceProtocols.Api;

    /// <summary>
    /// An S3 source is polled, but not over a mailbox: it has a bucket and a region where the
    /// mail protocols have a host and a port, and it may legitimately carry no credential at
    /// all when the ambient chain supplies one.
    /// </summary>
    private static bool IsBucket(string protocol) => protocol == ReportSourceProtocols.S3;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReportSourceDto>> ListAsync(CancellationToken ct)
    {
        return await db.ReportSources
            .AsNoTracking()
            .Include(x => x.DefaultClient)
            .OrderBy(x => x.Name)
            .Select(x => ToDto(x, x.DefaultClient != null ? x.DefaultClient.Name : null))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ServiceResult<ReportSourceDto>> CreateAsync(CreateReportSourceRequest request, CancellationToken ct)
    {
        var protocol = request.Protocol?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SupportedProtocols.Contains(protocol))
        {
            return ServiceResult<ReportSourceDto>.Failure(ProtocolError, 400);
        }

        if (currentUser.IsService && protocol != "api")
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "service credentials may create API report sources only", 403);
        }

        var pushed = IsPushed(protocol);
        var bucket = IsBucket(protocol);
        var mailbox = ReportSourceProtocols.IsMailbox(protocol);

        if (string.IsNullOrWhiteSpace(request.Name) ||
            request.DefaultClientId == Guid.Empty)
        {
            return ServiceResult<ReportSourceDto>.Failure("name and defaultClientId are required", 400);
        }

        if (mailbox && !HasCompleteMailboxConfiguration(
                request.Host, request.Port, request.UseTls, request.Username, request.Password))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "host, port, useTls, username, and password are required for mailbox sources", 400);
        }

        // Refused rather than ignored. Accepting mailbox settings on a source that will
        // never connect to a mailbox would leave a password sitting in the database that
        // nothing will ever use and nobody will remember is there.
        if (!mailbox && (!string.IsNullOrWhiteSpace(request.Host) || request.Port > 0))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                $"a source with protocol '{protocol}' has no mailbox and takes no host or port", 400);
        }

        if (pushed && (!string.IsNullOrWhiteSpace(request.Username) ||
            !string.IsNullOrWhiteSpace(request.Password)))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "an api source is pushed to and takes no host, username or password", 400);
        }

        if (pushed && request.DeleteAfterRetention)
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "mailbox retention cannot be enabled for an API source", 400);
        }

        if (bucket && string.IsNullOrWhiteSpace(request.S3Bucket))
        {
            return ServiceResult<ReportSourceDto>.Failure("an s3 source requires s3Bucket", 400);
        }

        // Half a credential is the dangerous shape: it looks configured and authenticates as
        // nobody. Either both halves, or neither and the ambient chain — never one.
        if (bucket && string.IsNullOrWhiteSpace(request.Username) != string.IsNullOrWhiteSpace(request.Password))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "an s3 source needs both username (access key id) and password (secret access key), " +
                "or neither to use the ambient credential chain", 400);
        }

        if (!bucket && (!string.IsNullOrWhiteSpace(request.S3Bucket) ||
            !string.IsNullOrWhiteSpace(request.S3Prefix) ||
            !string.IsNullOrWhiteSpace(request.S3Region) ||
            !string.IsNullOrWhiteSpace(request.S3Endpoint)))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                $"a source with protocol '{protocol}' takes no s3 settings", 400);
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
            Host = mailbox ? request.Host!.Trim().ToLowerInvariant() : null,
            Port = mailbox ? request.Port : null,

            // TLS is not a choice on a bucket: the SDK speaks HTTPS to AWS, and to a custom
            // endpoint it does whatever that endpoint's scheme says. Recorded as true so the
            // console does not display an S3 source as if it were sending a password in the
            // clear. Null on a pushed source, like every other inapplicable field.
            UseTls = mailbox ? request.UseTls : (bucket ? true : null),
            Username = pushed ? null : NullIfBlank(request.Username),
            PasswordEncrypted = pushed || string.IsNullOrWhiteSpace(request.Password)
                ? null
                : credentialProtector.Protect(request.Password),
            S3Bucket = bucket ? request.S3Bucket!.Trim() : null,
            S3Prefix = bucket ? NullIfBlank(request.S3Prefix) : null,
            S3Region = bucket ? NullIfBlank(request.S3Region) : null,
            S3Endpoint = bucket ? NullIfBlank(request.S3Endpoint) : null,
            S3ForcePathStyle = !bucket || request.S3ForcePathStyle,
            DefaultClientId = request.DefaultClientId,
            IsActive = request.IsActive,
            DeleteAfterRetention = !pushed && request.DeleteAfterRetention,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        source.NormalizeProtocolState();

        db.ReportSources.Add(source);
        await db.SaveChangesAsync(ct);

        return ServiceResult<ReportSourceDto>.Success(ToDto(source, null));
    }

    /// <inheritdoc />
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

            // Unchanged is always allowed, even when the value is no longer one that can
            // be created: a row holding a retired value would otherwise be uneditable, since
            // every save resends its own protocol. Only a *change* has to land on something
            // supported. Assigned to the outer variable (rather than shadowing it) because
            // every check below judges the protocol as it stands after this block.
            var unchanged = string.Equals(protocol, source.Protocol, StringComparison.Ordinal);
            if (!unchanged && !SupportedProtocols.Contains(protocol))
            {
                return ServiceResult<ReportSourceDto>.Failure(ProtocolError, 400);
            }
        }

        // Read once, off the protocol as it stands after the block above — whether or not
        // this request touched it — since every check below needs to judge the final row,
        // not just the fields this particular request happened to mention.
        var mailbox = ReportSourceProtocols.IsMailbox(protocol);
        var bucket = IsBucket(protocol);
        var pushed = IsPushed(protocol);

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

        if (mailbox && request.Host is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Host))
            {
                return ServiceResult<ReportSourceDto>.Failure("host cannot be empty", 400);
            }

            host = request.Host.Trim().ToLowerInvariant();
        }

        if (mailbox && request.Port.HasValue)
        {
            if (request.Port.Value <= 0)
            {
                return ServiceResult<ReportSourceDto>.Failure("port must be greater than 0", 400);
            }

            port = request.Port.Value;
        }

        if (!pushed && request.Username is not null)
        {
            // Blank is refused everywhere except a bucket, where it is how a stored access
            // key is handed back to the ambient credential chain — the same meaning it has
            // on create. Stored as null, the one representation for absent.
            if (string.IsNullOrWhiteSpace(request.Username) && !bucket)
            {
                return ServiceResult<ReportSourceDto>.Failure("username cannot be empty", 400);
            }

            username = NullIfBlank(request.Username);
        }

        if (!pushed && request.Password is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Password) && !bucket)
            {
                return ServiceResult<ReportSourceDto>.Failure("password cannot be empty", 400);
            }

            passwordEncrypted = string.IsNullOrWhiteSpace(request.Password)
                ? null
                : credentialProtector.Protect(request.Password);
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

        if (mailbox && request.UseTls.HasValue)
        {
            useTls = request.UseTls.Value;
        }

        if (request.IsActive.HasValue)
        {
            source.IsActive = request.IsActive.Value;
        }

        // Forced false on a pushed source rather than merely refused when true: an API
        // source never deletes anything, so there is no true to preserve.
        var deleteAfterRetention = pushed ? false : (request.DeleteAfterRetention ?? source.DeleteAfterRetention);
        if (pushed && request.DeleteAfterRetention == true)
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "mailbox retention cannot be enabled for an API source", 400);
        }

        if (request.S3Bucket is not null)
        {
            if (string.IsNullOrWhiteSpace(request.S3Bucket))
            {
                return ServiceResult<ReportSourceDto>.Failure("s3Bucket cannot be empty", 400);
            }

            source.S3Bucket = request.S3Bucket.Trim();
        }

        // Blank clears rather than being refused, unlike the bucket: an empty prefix is a
        // meaningful setting — poll the whole bucket — and so is dropping a custom endpoint
        // to go back to AWS.
        if (request.S3Prefix is not null)
        {
            source.S3Prefix = NullIfBlank(request.S3Prefix);
        }

        if (request.S3Region is not null)
        {
            source.S3Region = NullIfBlank(request.S3Region);
        }

        if (request.S3Endpoint is not null)
        {
            source.S3Endpoint = NullIfBlank(request.S3Endpoint);
        }

        if (request.S3ForcePathStyle.HasValue)
        {
            source.S3ForcePathStyle = request.S3ForcePathStyle.Value;
        }

        // Checked before anything is assigned, on the values as they will be saved: the
        // protocol and the fields that do or do not belong to it can arrive in the same
        // request in either order, and a refused update must leave the tracked row
        // untouched. What Create requires cannot be invented here, so these are refused
        // rather than cleared: there is no host to fall back to for a mailbox, and no
        // bucket name for an s3 source, that the row does not already carry.
        if (mailbox && !HasCompleteMailboxConfiguration(host, port, useTls, username, passwordEncrypted))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "a mailbox source requires host, port, useTls, username and password", 400);
        }

        if (bucket && string.IsNullOrWhiteSpace(source.S3Bucket))
        {
            return ServiceResult<ReportSourceDto>.Failure("an s3 source requires s3Bucket", 400);
        }

        // Half a credential is the dangerous shape: it looks configured and authenticates as
        // nobody. Either both halves, or neither and the ambient chain — never one. Same
        // rule as create, re-applied here because a PATCH can produce this shape create
        // never could: one field cleared, the other left as whatever it already was.
        // Read off the locals rather than the row for the same no-mutation reason.
        if (bucket && string.IsNullOrWhiteSpace(username) != string.IsNullOrEmpty(passwordEncrypted))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "an s3 source needs both username (access key id) and password (secret access key), " +
                "or neither to use the ambient credential chain", 400);
        }

        // Leaving an API source revokes its credentials: they authenticate nothing else,
        // and a row that changed protocol must not keep a live key from the old one.
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

        // What no longer belongs to the final protocol is cleared rather than refused.
        // The console already drops these fields from the request for the protocol it is
        // switching to — see ReportSourcesPage.tsx — so refusing a value merely left over
        // from the protocol being switched away from would reject an ordinary protocol
        // change the console itself sends.
        source.NormalizeProtocolState();

        source.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<ReportSourceDto>.Success(ToDto(source, null));
    }

    /// <summary>
    /// Blank is stored as null, so "not set" has one representation rather than two. Every
    /// reader of these columns treats null as absent, and a column that can also hold an
    /// empty string is one every reader has to check twice.
    /// </summary>
    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
            x.LastProcessedUidl,
            x.S3Bucket,
            x.S3Prefix,
            x.S3Region,
            x.S3Endpoint,
            x.S3ForcePathStyle,
            x.LastProcessedObjectAtUtc,
            x.LastProcessedObjectKey,
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
