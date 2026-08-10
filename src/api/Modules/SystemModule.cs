using Carter;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Hosting;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class SystemModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Resolved once at startup, not per request: APP_MODE cannot change while
        // the process runs, and Parse throws on a bad value — which must stay a
        // startup crash rather than becoming a 500 on this endpoint.
        var mode = AppRuntimeMode.FromEnvironment();

        app.MapGet("/api/v1/system/status", () =>
        {
            return Results.Ok(new
            {
                service = "dmarc-analyzer-api",
                // Was hardcoded "api", so an APP_MODE=all container — the shape
                // this project recommends, and the one the chart and Render both
                // deploy — reported the one mode it was not running in.
                mode = mode.ToName(),
                version = AppVersion.Current.Version,
                revision = AppVersion.Current.Revision,
                timestampUtc = DateTime.UtcNow,
            });
        }).AllowClientViewer();
    }
}
