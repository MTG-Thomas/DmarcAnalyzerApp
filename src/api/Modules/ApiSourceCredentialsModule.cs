using Carter;
using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class ApiSourceCredentialsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/report-sources/{sourceId:guid}/credentials", async (
            Guid sourceId,
            IApiSourceCredentialService service,
            CancellationToken ct) =>
        {
            var result = await service.ListAsync(sourceId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.NotFound();
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/report-sources/{sourceId:guid}/credentials", async (
            Guid sourceId,
            IApiSourceCredentialService service,
            IAuditLog audit,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await service.IssueAsync(sourceId, ct);
            if (!result.IsSuccess)
            {
                return result.StatusCode == 404
                    ? Results.NotFound()
                    : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var issued = result.Value!;
            await audit.RecordAsync(
                AuditEvents.ApiSourceCredentialCreated,
                "Issued an API source credential",
                "mailbox_source",
                sourceId,
                ct: ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created(
                $"/api/v1/report-sources/{sourceId}/credentials/{issued.Id}",
                issued);
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/report-sources/{sourceId:guid}/credentials/rotate", async (
            Guid sourceId,
            IApiSourceCredentialService service,
            IAuditLog audit,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await service.IssueAsync(sourceId, ct);
            if (!result.IsSuccess)
            {
                return result.StatusCode == 404
                    ? Results.NotFound()
                    : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            await audit.RecordAsync(
                AuditEvents.ApiSourceCredentialRotated,
                "Rotated an API source credential; prior credentials remain active until revoked",
                "mailbox_source",
                sourceId,
                ct: ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result.Value);
        }).RequireAgencyAdmin();

        app.MapDelete("/api/v1/report-sources/{sourceId:guid}/credentials/{credentialId:guid}", async (
            Guid sourceId,
            Guid credentialId,
            IApiSourceCredentialService service,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var result = await service.RevokeAsync(sourceId, credentialId, ct);
            if (!result.IsSuccess)
            {
                return Results.NotFound();
            }

            await audit.RecordAsync(
                AuditEvents.ApiSourceCredentialRevoked,
                "Revoked an API source credential",
                "mailbox_source",
                sourceId,
                ct: ct);
            return Results.Ok(result.Value);
        }).RequireAgencyAdmin();
    }
}
