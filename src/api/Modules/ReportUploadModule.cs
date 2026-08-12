using Carter;
using DmarcAnalyzer.Api.Application.Ingestion;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace DmarcAnalyzer.Api.Modules;

public sealed class ReportUploadModule : ICarterModule
{
    public const string RateLimitPolicy = "report-ingest";

    public static string CredentialRateLimitPartition(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? $"credential:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..].Trim())))}"
            : $"address:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    public static FixedWindowRateLimiterOptions RateLimiterOptions(ReportPayloadExtractionOptions limits)
        => new()
        {
            PermitLimit = limits.RateLimitPermits,
            Window = TimeSpan.FromSeconds(limits.RateLimitWindowSeconds),
            QueueLimit = 0,
        };

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/ingest/v1/sources/{sourceId:guid}/reports",
            (Guid sourceId, HttpContext context, ReportUploadHandler handler, CancellationToken ct)
                => handler.HandleAsync(context, sourceId, ct))
            .RequireRateLimiting(RateLimitPolicy);
    }
}
