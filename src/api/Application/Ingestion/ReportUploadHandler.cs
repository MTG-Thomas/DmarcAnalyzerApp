using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.Audit;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using AuthenticationHeaderValue = System.Net.Http.Headers.AuthenticationHeaderValue;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed record ReportUploadResponse(
    int Inserted,
    int Duplicates,
    int Rejected,
    IReadOnlyList<string> RejectionCodes);

/// <summary>
/// The HTTP trust boundary for machine uploads. Authentication constructs the
/// trusted source context; request data can never provide or replace its client.
/// </summary>
public sealed class ReportUploadHandler(
    IApiSourceAuthenticator authenticator,
    IReportPayloadIngestor ingestor,
    IOptions<ReportPayloadExtractionOptions> options,
    IAuditLog audit)
{
    private const string ContentSha256Header = "X-Content-SHA256";
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private readonly ReportPayloadExtractionOptions _limits = options.Value;

    public async Task<IResult> HandleAsync(
        HttpContext context,
        Guid sourceId,
        CancellationToken ct)
    {
        var token = GetBearerToken(context.Request);
        var source = await authenticator.AuthenticateAsync(sourceId, token, ct);
        if (source is null)
        {
            return Response(401, "Unauthorized");
        }

        if (context.Request.ContentLength is > 0
            && context.Request.ContentLength > _limits.MaxRequestBytes)
        {
            return await AuditedResponseAsync(sourceId, 413, "RequestTooLarge", ct);
        }

        if (!TryGetExpectedDigest(context.Request.Headers, out var expectedDigest, out var headerRejection))
        {
            return await AuditedResponseAsync(sourceId, 422, headerRejection, ct);
        }

        Stream payload;
        string? fileName = null;
        string? mediaType = context.Request.ContentType;
        IAsyncDisposable? ownedPayload = null;

        if (IsMultipart(context.Request.ContentType))
        {
            if (!HasValidMultipartBoundary(context.Request.ContentType))
            {
                return await AuditedResponseAsync(sourceId, 415, "UnsupportedMultipartBody", ct);
            }

            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(new FormOptions
                {
                    MultipartBodyLengthLimit = _limits.MaxRequestBytes,
                    ValueCountLimit = 1,
                }, ct);
            }
            catch (InvalidDataException ex) when (IsBodyLimitExceeded(ex))
            {
                return await AuditedResponseAsync(sourceId, 413, "RequestTooLarge", ct);
            }
            catch (InvalidDataException)
            {
                return await AuditedResponseAsync(sourceId, 415, "UnsupportedMultipartBody", ct);
            }

            if (form.Count != 0 || form.Files.Count != 1)
            {
                return await AuditedResponseAsync(sourceId, 415, "UnsupportedMultipartBody", ct);
            }

            var file = form.Files[0];
            if (file.Length > _limits.MaxRequestBytes)
            {
                return await AuditedResponseAsync(sourceId, 413, "RequestTooLarge", ct);
            }

            payload = file.OpenReadStream();
            ownedPayload = payload;
            fileName = file.FileName;
            mediaType = file.ContentType;
        }
        else if (IsOtherMultipart(context.Request.ContentType))
        {
            return await AuditedResponseAsync(sourceId, 415, "UnsupportedMediaType", ct);
        }
        else
        {
            payload = context.Request.Body;
        }

        try
        {
            var result = await ingestor.IngestAsync(
                source,
                payload,
                new ReportPayloadMetadata(fileName, mediaType, expectedDigest),
                ct);

            var inserted = result.DmarcInserted + result.TlsInserted;
            var duplicates = result.DmarcDuplicates + result.TlsDuplicates;
            var codes = result.Rejections.Select(x => x.Code.ToString()).ToArray();

            if (inserted > 0)
            {
                return await AuditedResponseAsync(
                    sourceId, 201, inserted, duplicates, codes, ct);
            }

            if (duplicates > 0)
            {
                return await AuditedResponseAsync(
                    sourceId, 200, inserted, duplicates, codes, ct);
            }

            var statusCode = StatusFor(result.Rejections);
            return await AuditedResponseAsync(
                sourceId, statusCode, inserted, duplicates, codes, ct);
        }
        finally
        {
            if (ownedPayload is not null)
            {
                await ownedPayload.DisposeAsync();
            }
        }
    }

    private async Task<IResult> AuditedResponseAsync(
        Guid sourceId,
        int statusCode,
        string rejectionCode,
        CancellationToken ct)
        => await AuditedResponseAsync(sourceId, statusCode, 0, 0, [rejectionCode], ct);

    private async Task<IResult> AuditedResponseAsync(
        Guid sourceId,
        int statusCode,
        int inserted,
        int duplicates,
        IReadOnlyList<string> rejectionCodes,
        CancellationToken ct)
    {
        await audit.RecordSystemAsync(
            AuditEvents.ApiSourceReportUploaded,
            $"API source report upload completed with HTTP {statusCode}",
            $"sourceId={sourceId}; inserted={inserted}; duplicates={duplicates}; rejected={rejectionCodes.Count}",
            ct: ct);

        return Results.Json(
            new ReportUploadResponse(inserted, duplicates, rejectionCodes.Count, rejectionCodes),
            statusCode: statusCode);
    }

    private static IResult Response(int statusCode, string rejectionCode)
        => Results.Json(
            new ReportUploadResponse(0, 0, 1, [rejectionCode]),
            statusCode: statusCode);

    private static string? GetBearerToken(HttpRequest request)
    {
        if (request.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
        {
            return null;
        }

        return header.Parameter;
    }

    private static bool TryGetExpectedDigest(
        IHeaderDictionary headers,
        out string? digest,
        out string rejectionCode)
    {
        digest = null;
        rejectionCode = string.Empty;

        var contentDigestValues = headers[ContentSha256Header];
        if (contentDigestValues.Count > 1)
        {
            rejectionCode = "InvalidContentSha256";
            return false;
        }

        var suppliedDigest = contentDigestValues.Count == 1
            ? contentDigestValues.ToString()
            : null;
        if (suppliedDigest is not null
            && (suppliedDigest.Length != 64 || suppliedDigest.Any(c => !Uri.IsHexDigit(c))))
        {
            rejectionCode = "InvalidContentSha256";
            return false;
        }

        var idempotencyValues = headers[IdempotencyKeyHeader];
        if (idempotencyValues.Count > 1)
        {
            rejectionCode = "InvalidIdempotencyKey";
            return false;
        }

        var idempotencyKey = idempotencyValues.Count == 1
            ? idempotencyValues.ToString()
            : null;
        string? idempotencyDigest = null;
        if (idempotencyKey is not null)
        {
            if (!idempotencyKey.StartsWith("sha256:", StringComparison.Ordinal)
                || idempotencyKey.Length != 71
                || idempotencyKey[7..].Any(c => !Uri.IsHexDigit(c)))
            {
                rejectionCode = "InvalidIdempotencyKey";
                return false;
            }

            idempotencyDigest = idempotencyKey[7..];
        }

        if (suppliedDigest is not null
            && idempotencyDigest is not null
            && !string.Equals(suppliedDigest, idempotencyDigest, StringComparison.OrdinalIgnoreCase))
        {
            rejectionCode = "InvalidIdempotencyKey";
            return false;
        }

        digest = (suppliedDigest ?? idempotencyDigest)?.ToLowerInvariant();
        return true;
    }

    private static int StatusFor(IReadOnlyList<ReportPayloadRejection> rejections)
    {
        if (rejections.Any(x => x.Code is
                ReportPayloadRejectionCode.RequestTooLarge
                or ReportPayloadRejectionCode.ArchiveEntryLimitExceeded
                or ReportPayloadRejectionCode.EntryTooLarge
                or ReportPayloadRejectionCode.ExpandedSizeLimitExceeded
                or ReportPayloadRejectionCode.CompressionRatioExceeded))
        {
            return 413;
        }

        if (rejections.Any(x => x.Code is
                ReportPayloadRejectionCode.UnsupportedFormat
                or ReportPayloadRejectionCode.UnsupportedContainer
                or ReportPayloadRejectionCode.NestedContainer))
        {
            return 415;
        }

        return 422;
    }

    private static bool IsMultipart(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
           && string.Equals(parsed.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidMultipartBoundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            return false;
        }

        var boundary = HeaderUtilities.RemoveQuotes(parsed.Boundary).Value;
        return !string.IsNullOrWhiteSpace(boundary)
               && boundary.Length <= new FormOptions().MultipartBoundaryLengthLimit;
    }

    private static bool IsBodyLimitExceeded(InvalidDataException exception)
        => exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase);

    private static bool IsOtherMultipart(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
           && parsed.MediaType.Value?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true;
}
