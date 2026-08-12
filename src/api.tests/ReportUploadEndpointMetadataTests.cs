using System.Net;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    [Fact]
    public void RateLimitPartitionsByCredentialWithAnAddressFallback()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer secret-token";

        var credential = ReportUploadModule.CredentialRateLimitPartition(context);
        Assert.StartsWith("credential:", credential, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", credential, StringComparison.Ordinal);

        context.Request.Headers.Remove("Authorization");
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.Equal("address:127.0.0.1", ReportUploadModule.CredentialRateLimitPartition(context));
    }

    [Fact]
    public void RateLimiterUsesConfiguredPermitCountAndWindowWithoutQueuing()
    {
        var options = ReportUploadModule.RateLimiterOptions(new ReportPayloadExtractionOptions
        {
            RateLimitPermits = 7,
            RateLimitWindowSeconds = 13,
        });

        Assert.Equal(7, options.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(13), options.Window);
        Assert.Equal(0, options.QueueLimit);
    }
}
