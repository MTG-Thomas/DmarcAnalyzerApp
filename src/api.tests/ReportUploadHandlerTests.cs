using System.Net.Http.Headers;
using System.Security.Cryptography;
using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Contracts.Auth;
using DmarcAnalyzer.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ReportUploadHandlerTests
{
    private static readonly Guid SourceId = Guid.NewGuid();
    private static readonly ReportSourceContext Source = new(SourceId, Guid.NewGuid());
    private const string Token = "dmarc_v1.abcdefghijklmnopqrstuv.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task SessionMiddlewareBypassStillRequiresMachineToken()
    {
        var ingestor = new StubIngestor(_ => Success(inserted: 1));
        var handler = Handler(authenticated: false, ingestor);
        var context = Request("<feedback />"u8.ToArray());
        context.Request.Headers.Cookie = "dmarc_session=valid-looking-cookie";
        IResult? endpointResult = null;
        var middleware = new SessionAuthMiddleware(async nextContext =>
        {
            endpointResult = await handler.HandleAsync(nextContext, SourceId, default);
        });

        await middleware.InvokeAsync(
            context,
            new ThrowingAuthService(),
            new ThrowingServiceApiAuthenticator(),
            new CurrentUserContext());

        Assert.Equal(401, Status(endpointResult!));
        Assert.Equal(["Unauthorized"], Response(endpointResult!).RejectionCodes);
        Assert.False(ingestor.WasCalled);
    }

    [Fact]
    public async Task IntegrityHeadersAreOptionalButEachSuppliedDigestIsVerified()
    {
        var bytes = "<feedback />"u8.ToArray();
        var digest = Sha256(bytes);
        var expectedDigests = new List<string?>();
        var ingestor = new StubIngestor(metadata =>
        {
            expectedDigests.Add(metadata.ExpectedContentSha256);
            return Success(inserted: 1);
        });
        var handler = Handler(authenticated: true, ingestor);

        var withoutHeaders = await handler.HandleAsync(Request(bytes), SourceId, default);
        var withIdempotency = Request(bytes);
        withIdempotency.Request.Headers["Idempotency-Key"] = $"sha256:{digest}";
        var withIdempotencyResult = await handler.HandleAsync(withIdempotency, SourceId, default);

        Assert.Equal(201, Status(withoutHeaders));
        Assert.Equal(201, Status(withIdempotencyResult));
        Assert.Equal([null, digest], expectedDigests);
    }

    [Fact]
    public async Task IncoherentIntegrityHeadersAreRejectedBeforeReadingBody()
    {
        var bytes = "<feedback />"u8.ToArray();
        var context = Request(bytes);
        context.Request.Headers["X-Content-SHA256"] = Sha256(bytes);
        context.Request.Headers["Idempotency-Key"] = $"sha256:{new string('0', 64)}";
        var ingestor = new StubIngestor(_ => Success(inserted: 1));

        var result = await Handler(authenticated: true, ingestor)
            .HandleAsync(context, SourceId, default);

        Assert.Equal(422, Status(result));
        Assert.Equal(["InvalidIdempotencyKey"], Response(result).RejectionCodes);
        Assert.False(ingestor.WasCalled);
    }

    [Fact]
    public async Task MalformedAuthorizationVariantsAreUniformlyUnauthorized()
    {
        var contexts = new[]
        {
            Request("payload"u8.ToArray()),
            Request("payload"u8.ToArray()),
            Request("payload"u8.ToArray()),
            Request("payload"u8.ToArray()),
        };
        contexts[0].Request.Headers.Remove("Authorization");
        contexts[1].Request.Headers.Authorization = "not a header";
        contexts[2].Request.Headers.Authorization = "Basic credentials";
        contexts[3].Request.Headers.Authorization = new StringValues([Token, Token]);
        var ingestor = new StubIngestor(_ => Success(inserted: 1));
        var handler = Handler(authenticated: true, ingestor);

        foreach (var context in contexts)
        {
            var result = await handler.HandleAsync(context, SourceId, default);
            Assert.Equal(401, Status(result));
            Assert.Equal(["Unauthorized"], Response(result).RejectionCodes);
        }

        Assert.False(ingestor.WasCalled);
    }

    [Fact]
    public async Task MalformedIntegrityHeaderVariantsAreRejectedBeforeReadingBody()
    {
        var contexts = new[]
        {
            Request("payload"u8.ToArray()),
            Request("payload"u8.ToArray()),
            Request("payload"u8.ToArray()),
            Request("payload"u8.ToArray()),
            Request("payload"u8.ToArray()),
        };
        contexts[0].Request.Headers["X-Content-SHA256"] = "short";
        contexts[1].Request.Headers["X-Content-SHA256"] = new string('g', 64);
        contexts[2].Request.Headers["X-Content-SHA256"] = new StringValues([new string('a', 64), new string('b', 64)]);
        contexts[3].Request.Headers["Idempotency-Key"] = "not-sha256";
        contexts[4].Request.Headers["Idempotency-Key"] = new StringValues(["sha256:" + new string('a', 64), "sha256:" + new string('b', 64)]);
        var ingestor = new StubIngestor(_ => Success(inserted: 1));
        var handler = Handler(authenticated: true, ingestor);
        var results = new List<IResult>();

        foreach (var context in contexts)
        {
            var result = await handler.HandleAsync(context, SourceId, default);
            results.Add(result);
            Assert.Equal(422, Status(result));
        }

        Assert.Equal(["InvalidContentSha256"], Response(results[0]).RejectionCodes);
        Assert.Equal(["InvalidIdempotencyKey"], Response(results[3]).RejectionCodes);
        Assert.False(ingestor.WasCalled);
    }

    [Fact]
    public async Task DigestMismatchIsAStableUnprocessableResponse()
    {
        var bytes = "<feedback />"u8.ToArray();
        var context = Request(bytes);
        SetIntegrityHeaders(context, Sha256(bytes));
        var ingestor = new StubIngestor(_ => new(
            0, 0, 0, 0, 0, 0,
            [new(ReportPayloadRejectionCode.ContentSha256Mismatch)],
            Sha256("different"u8.ToArray()),
            bytes.Length,
            ReportPayloadContainer.Bare));

        var result = await Handler(authenticated: true, ingestor)
            .HandleAsync(context, SourceId, default);

        Assert.Equal(422, Status(result));
        Assert.Equal(["ContentSha256Mismatch"], Response(result).RejectionCodes);
    }

    [Fact]
    public async Task PartialContainersReturnCreatedWithSafeCountsAndCodesOnly()
    {
        var resultValue = new ReportPayloadIngestResult(
            1, 0, 0, 0, 0, 0,
            [new(ReportPayloadRejectionCode.UnsupportedFormat, "customer-report-name.txt")],
            new string('a', 64),
            100,
            ReportPayloadContainer.Zip);
        var result = await Handler(authenticated: true, new StubIngestor(_ => resultValue))
            .HandleAsync(Request("zip"u8.ToArray()), SourceId, default);

        Assert.Equal(201, Status(result));
        var response = Response(result);
        Assert.Equal(1, response.Inserted);
        Assert.Equal(1, response.Rejected);
        Assert.Equal(["UnsupportedFormat"], response.RejectionCodes);
        Assert.DoesNotContain("customer", string.Join(',', response.RejectionCodes));
    }

    [Fact]
    public async Task MultipartRequiresBoundaryAndExactlyOneFile()
    {
        var handler = Handler(authenticated: true, new StubIngestor(_ => Success(inserted: 1)));
        var missingBoundary = Request([]);
        missingBoundary.Request.ContentType = "multipart/form-data";

        var missingBoundaryResult = await handler.HandleAsync(missingBoundary, SourceId, default);

        Assert.Equal(415, Status(missingBoundaryResult));
        Assert.Equal(["UnsupportedMultipartBody"], Response(missingBoundaryResult).RejectionCodes);

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent("one"u8.ToArray()), "first", "one.xml");
        multipart.Add(new ByteArrayContent("two"u8.ToArray()), "second", "two.xml");
        var twoFiles = await MultipartRequestAsync(multipart);
        var twoFilesResult = await handler.HandleAsync(twoFiles, SourceId, default);

        Assert.Equal(415, Status(twoFilesResult));
    }

    [Fact]
    public async Task MultipartUsesPlatformParserAndPassesFileBytesUnchanged()
    {
        var bytes = "<feedback>unchanged</feedback>"u8.ToArray();
        byte[]? captured = null;
        var ingestor = new StubIngestor(async (_, payload, _) =>
        {
            using var output = new MemoryStream();
            await payload.CopyToAsync(output);
            captured = output.ToArray();
            return Success(inserted: 1);
        });
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        multipart.Add(file, "report", "report.xml");
        var context = await MultipartRequestAsync(multipart);

        var result = await Handler(authenticated: true, ingestor)
            .HandleAsync(context, SourceId, default);

        Assert.Equal(201, Status(result));
        Assert.Equal(bytes, captured);
    }

    [Fact]
    public async Task MultipartActualOversizeIs413WhenContentLengthLies()
    {
        const int maxBytes = 64;
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(new byte[maxBytes + 1]), "report", "report.xml");
        var context = await MultipartRequestAsync(multipart);
        context.Request.ContentLength = 1;
        var ingestor = new StubIngestor(_ => Success(inserted: 1));

        var result = await Handler(authenticated: true, ingestor, maxBytes)
            .HandleAsync(context, SourceId, default);

        Assert.Equal(413, Status(result));
        Assert.Equal(["RequestTooLarge"], Response(result).RejectionCodes);
        Assert.False(ingestor.WasCalled);
    }

    [Fact]
    public async Task EarlyLengthAndUnsupportedMultipartMediaReturnStableStatuses()
    {
        const int maxBytes = 64;
        var oversized = Request(new byte[maxBytes + 1]);
        var multipartMixed = Request("payload"u8.ToArray());
        multipartMixed.Request.ContentType = "multipart/mixed; boundary=test";
        var ingestor = new StubIngestor(_ => Success(inserted: 1));
        var handler = Handler(authenticated: true, ingestor, maxBytes);

        var oversizedResult = await handler.HandleAsync(oversized, SourceId, default);
        var mediaResult = await handler.HandleAsync(multipartMixed, SourceId, default);

        Assert.Equal(413, Status(oversizedResult));
        Assert.Equal(["RequestTooLarge"], Response(oversizedResult).RejectionCodes);
        Assert.Equal(415, Status(mediaResult));
        Assert.Equal(["UnsupportedMediaType"], Response(mediaResult).RejectionCodes);
        Assert.False(ingestor.WasCalled);
    }

    [Theory]
    [InlineData(ReportPayloadRejectionCode.RequestTooLarge, 413)]
    [InlineData(ReportPayloadRejectionCode.UnsupportedContainer, 415)]
    [InlineData(ReportPayloadRejectionCode.InvalidDmarcReport, 422)]
    public async Task TypedIngestRejectionsMapToStableStatuses(
        ReportPayloadRejectionCode code,
        int expectedStatus)
    {
        var resultValue = new ReportPayloadIngestResult(
            0, 0, 0, 0, 0, 0,
            [new(code)],
            new string('a', 64),
            100,
            ReportPayloadContainer.Bare);

        var result = await Handler(authenticated: true, new StubIngestor(_ => resultValue))
            .HandleAsync(Request("payload"u8.ToArray()), SourceId, default);

        Assert.Equal(expectedStatus, Status(result));
        Assert.Equal([code.ToString()], Response(result).RejectionCodes);
    }

    [Fact]
    public async Task MultipartRejectsSecondSectionBeforeReadingTheRemainingBody()
    {
        const int maxBytes = 1024 * 1024;
        using var multipart = new MultipartFormDataContent();
        for (var index = 0; index < 100; index++)
        {
            multipart.Add(
                new ByteArrayContent(new byte[32]),
                $"report{index}",
                $"report{index}.xml");
        }

        var context = await MultipartRequestAsync(multipart);
        context.Request.ContentLength = null;
        var body = Assert.IsType<MemoryStream>(context.Request.Body);
        var ingestor = new StubIngestor(_ => Success(inserted: 1));

        var result = await Handler(authenticated: true, ingestor, maxBytes)
            .HandleAsync(context, SourceId, default);

        Assert.Equal(415, Status(result));
        Assert.Equal(["UnsupportedMultipartBody"], Response(result).RejectionCodes);
        Assert.True(body.Position < body.Length);
        Assert.False(ingestor.WasCalled);
    }

    [Fact]
    public async Task AuditContainsOnlySourceOutcomeAndCounts()
    {
        var bytes = "<feedback>private-payload</feedback>"u8.ToArray();
        var digest = Sha256(bytes);
        var context = Request(bytes);
        SetIntegrityHeaders(context, digest);
        var audit = new RecordingAuditLog();

        var result = await Handler(
                authenticated: true,
                new StubIngestor(_ => Success(inserted: 1)),
                audit: audit)
            .HandleAsync(context, SourceId, default);

        Assert.Equal(201, Status(result));
        Assert.Equal(AuditEvents.ApiSourceReportUploaded, audit.EventType);
        Assert.Contains(SourceId.ToString(), audit.Details);
        Assert.Contains("inserted=1", audit.Details);
        Assert.DoesNotContain(Token, $"{audit.Summary}{audit.Details}");
        Assert.DoesNotContain(digest, $"{audit.Summary}{audit.Details}");
        Assert.DoesNotContain("private-payload", $"{audit.Summary}{audit.Details}");
    }

    private static ReportUploadHandler Handler(
        bool authenticated,
        StubIngestor ingestor,
        int maxRequestBytes = 1024 * 1024,
        IAuditLog? audit = null)
        => new(
            new StubAuthenticator(authenticated ? Source : null),
            ingestor,
            Options.Create(new ReportPayloadExtractionOptions
            {
                MaxRequestBytes = maxRequestBytes,
                MaxEntryBytes = maxRequestBytes,
                MaxExpandedBytes = maxRequestBytes,
            }),
            audit ?? new NullAuditLog());

    private static DefaultHttpContext Request(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body, writable: false);
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/octet-stream";
        context.Request.Headers.Authorization = $"Bearer {Token}";
        return context;
    }

    private static async Task<DefaultHttpContext> MultipartRequestAsync(MultipartFormDataContent multipart)
    {
        var bytes = await multipart.ReadAsByteArrayAsync();
        var context = Request(bytes);
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        return context;
    }

    private static void SetIntegrityHeaders(DefaultHttpContext context, string digest)
    {
        context.Request.Headers["X-Content-SHA256"] = digest;
        context.Request.Headers["Idempotency-Key"] = $"sha256:{digest}";
    }

    private static int Status(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static ReportUploadResponse Response(IResult result)
        => Assert.IsType<ReportUploadResponse>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static string Sha256(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ReportPayloadIngestResult Success(int inserted = 0, int duplicates = 0)
        => new(
            inserted, duplicates, 0, 0, 0, 0,
            [], null, 0, ReportPayloadContainer.Bare);

    private sealed class StubAuthenticator(ReportSourceContext? result) : IApiSourceAuthenticator
    {
        public Task<ReportSourceContext?> AuthenticateAsync(Guid sourceId, string? bearerToken, CancellationToken ct)
            => Task.FromResult(bearerToken == Token ? result : null);
    }

    private sealed class StubIngestor : IReportPayloadIngestor
    {
        private readonly Func<ReportPayloadMetadata, ReportPayloadIngestResult>? _sync;
        private readonly Func<ReportPayloadMetadata, Stream, CancellationToken, Task<ReportPayloadIngestResult>>? _async;

        public StubIngestor(Func<ReportPayloadMetadata, ReportPayloadIngestResult> action) => _sync = action;

        public StubIngestor(Func<ReportPayloadMetadata, Stream, CancellationToken, Task<ReportPayloadIngestResult>> action)
            => _async = action;

        public bool WasCalled { get; private set; }

        public async Task<ReportPayloadIngestResult> IngestAsync(
            ReportSourceContext source,
            Stream payload,
            ReportPayloadMetadata metadata,
            CancellationToken ct)
        {
            WasCalled = true;
            return _async is not null
                ? await _async(metadata, payload, ct)
                : _sync!(metadata);
        }
    }

    private sealed class NullAuditLog : IAuditLog
    {
        public Task RecordAsync(string eventType, string summary, string? targetType = null, Guid? targetId = null, Guid? clientId = null, string? details = null, string? actorEmailOverride = null, Guid? actorUserIdOverride = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RecordSystemAsync(string eventType, string summary, string? details = null, Guid? clientId = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public string? EventType { get; private set; }
        public string? Summary { get; private set; }
        public string? Details { get; private set; }

        public Task RecordAsync(string eventType, string summary, string? targetType = null, Guid? targetId = null, Guid? clientId = null, string? details = null, string? actorEmailOverride = null, Guid? actorUserIdOverride = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Machine uploads must use system audit events.");

        public Task RecordSystemAsync(string eventType, string summary, string? details = null, Guid? clientId = null, CancellationToken ct = default)
        {
            EventType = eventType;
            Summary = summary;
            Details = details;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAuthService : IAuthService
    {
        private static Exception Unexpected() => new InvalidOperationException("Session auth must not run for machine ingestion.");
        public Task<bool> RequiresBootstrapAsync(CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<UserDto>> RegisterAsync(RegisterRequest request, CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<LoginResultDto>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<LoginResultDto>> LoginWithExternalIdentityAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct) => throw Unexpected();
        public Task LogoutAsync(string cookieId, CancellationToken ct) => throw Unexpected();
        public Task<UserDto?> GetCurrentUserAsync(string cookieId, CancellationToken ct) => throw Unexpected();
        public Task<SessionUserDto?> GetSessionUserAsync(string cookieId, CancellationToken ct) => throw Unexpected();
    }

    private sealed class ThrowingServiceApiAuthenticator : IServiceApiAuthenticator
    {
        public Task<ServiceApiPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct)
            => throw new InvalidOperationException("machine upload must bypass session API authentication");
    }
}
