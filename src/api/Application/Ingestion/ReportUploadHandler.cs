using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.Audit;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
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
            ApplyServerRequestLimit(context, _limits.MaxRequestBytes);

            if (!HasValidMultipartBoundary(context.Request.ContentType))
            {
                return await AuditedResponseAsync(sourceId, 415, "UnsupportedMultipartBody", ct);
            }

            try
            {
                var multipart = await ReadSingleMultipartFileAsync(context, ct);
                payload = multipart.Payload;
                ownedPayload = payload;
                fileName = multipart.FileName;
                mediaType = multipart.MediaType;
            }
            catch (MultipartUploadException ex)
            {
                return await AuditedResponseAsync(sourceId, ex.StatusCode, ex.RejectionCode, ct);
            }
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

    private async Task<MultipartUpload> ReadSingleMultipartFileAsync(
        HttpContext context,
        CancellationToken ct)
    {
        MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType);
        var boundary = HeaderUtilities.RemoveQuotes(mediaType!.Boundary).Value!;
        var requestBody = context.Request.Body;
        var boundedBody = new BoundedRequestBodyStream(
            requestBody,
            _limits.MaxRequestBytes);
        FileBufferingReadStream? bufferedFile = null;

        try
        {
            var reader = new MultipartReader(boundary, boundedBody)
            {
                BodyLengthLimit = _limits.MaxRequestBytes,
            };
            var section = await reader.ReadNextSectionAsync(ct);
            if (section is null
                || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
            {
                throw new MultipartUploadException(415, "UnsupportedMultipartBody");
            }

            var fileName = HeaderUtilities.RemoveQuotes(
                disposition.FileNameStar.HasValue
                    ? disposition.FileNameStar
                    : disposition.FileName).Value;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new MultipartUploadException(415, "UnsupportedMultipartBody");
            }

            bufferedFile = new FileBufferingReadStream(
                section.Body,
                memoryThreshold: 64 * 1024,
                bufferLimit: _limits.MaxRequestBytes,
                tempFileDirectory: Path.GetTempPath());
            await bufferedFile.CopyToAsync(Stream.Null, 64 * 1024, ct);
            bufferedFile.Position = 0;

            // The first file body is drained, so reading one more section is
            // enough to reject fields or extra files without buffering them.
            if (await reader.ReadNextSectionAsync(ct) is not null)
            {
                throw new MultipartUploadException(415, "UnsupportedMultipartBody");
            }

            var upload = new MultipartUpload(bufferedFile, fileName, section.ContentType);
            bufferedFile = null;
            return upload;
        }
        catch (RequestBodyTooLargeException)
        {
            throw new MultipartUploadException(413, "RequestTooLarge");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            throw new MultipartUploadException(413, "RequestTooLarge");
        }
        catch (InvalidDataException ex) when (IsBodyLimitExceeded(ex))
        {
            throw new MultipartUploadException(413, "RequestTooLarge");
        }
        catch (InvalidDataException)
        {
            throw new MultipartUploadException(415, "UnsupportedMultipartBody");
        }
        catch (IOException ex) when (
            ex.Message.Contains("Buffer limit", StringComparison.OrdinalIgnoreCase))
        {
            throw new MultipartUploadException(413, "RequestTooLarge");
        }
        finally
        {
            if (bufferedFile is not null)
            {
                await bufferedFile.DisposeAsync();
            }

            context.Request.Body = requestBody;
        }
    }

    private static void ApplyServerRequestLimit(HttpContext context, long maxBytes)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }
            && (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > maxBytes))
        {
            feature.MaxRequestBodySize = maxBytes;
        }
    }

    private static bool IsOtherMultipart(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
           && parsed.MediaType.Value?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true;

    private sealed class RequestBodyTooLargeException : IOException;

    private sealed class MultipartUploadException(
        int statusCode,
        string rejectionCode) : Exception
    {
        public int StatusCode { get; } = statusCode;
        public string RejectionCode { get; } = rejectionCode;
    }

    private sealed record MultipartUpload(
        Stream Payload,
        string FileName,
        string? MediaType);

    private sealed class BoundedRequestBodyStream(Stream inner, long maxBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Count(inner.Read(buffer, offset, BoundedCount(count)));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => Count(await inner.ReadAsync(
                buffer.AsMemory(offset, BoundedCount(count)),
                cancellationToken));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(
                buffer[..BoundedCount(buffer.Length)],
                cancellationToken));

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0)
            {
                Count(1);
            }

            return value;
        }

        private int BoundedCount(int requested)
            => (int)Math.Min(requested, Math.Max(1, maxBytes - _bytesRead + 1));

        private int Count(int read)
        {
            _bytesRead += read;
            if (_bytesRead > maxBytes)
            {
                throw new RequestBodyTooLargeException();
            }

            return read;
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken)
            => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
