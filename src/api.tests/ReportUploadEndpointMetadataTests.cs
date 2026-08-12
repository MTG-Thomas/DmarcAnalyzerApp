using DmarcAnalyzer.Api.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ReportUploadEndpointMetadataTests
{
    [Fact]
    public void UploadEndpointRequiresTheCredentialRateLimit()
    {
        var app = WebApplication.CreateBuilder().Build();
        new ReportUploadModule().AddRoutes(app);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == "/api/ingest/v1/sources/{sourceId:guid}/reports");

        Assert.Equal(
            ReportUploadModule.RateLimitPolicy,
            endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }
}
