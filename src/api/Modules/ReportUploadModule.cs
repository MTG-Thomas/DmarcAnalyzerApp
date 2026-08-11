using Carter;
using DmarcAnalyzer.Api.Application.Ingestion;

namespace DmarcAnalyzer.Api.Modules;

public sealed class ReportUploadModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/ingest/v1/sources/{sourceId:guid}/reports",
            (Guid sourceId, HttpContext context, ReportUploadHandler handler, CancellationToken ct)
                => handler.HandleAsync(context, sourceId, ct));
    }
}
