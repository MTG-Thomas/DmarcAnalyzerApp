using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;

namespace DmarcAnalyzer.Api.Modules;

public sealed class ServiceApiCredentialsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/service-credentials/permissions", () =>
            Results.Ok(ServiceApiPermissions.Catalog))
            .RequireAgencyAdmin();

        app.MapGet("/api/v1/service-credentials", async (
            IServiceApiCredentialService service,
            CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequireAgencyAdmin();

        app.MapPost("/api/v1/service-credentials", async (
            CreateServiceApiCredentialRequest request,
            IServiceApiCredentialService service,
            IAuditLog audit,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await service.IssueAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var issued = result.Value!;
            await audit.RecordAsync(
                AuditEvents.ServiceApiCredentialCreated,
                $"Issued service API credential {issued.Name}",
                "service_api_credential",
                issued.Id,
                ct: ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/v1/service-credentials/{issued.Id}", issued);
        }).RequireAgencyAdmin();

        app.MapDelete("/api/v1/service-credentials/{id:guid}", async (
            Guid id,
            IServiceApiCredentialService service,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var result = await service.RevokeAsync(id, ct);
            if (!result.IsSuccess)
            {
                return Results.NotFound();
            }

            await audit.RecordAsync(
                AuditEvents.ServiceApiCredentialRevoked,
                $"Revoked service API credential {result.Value!.Name}",
                "service_api_credential",
                id,
                ct: ct);
            return Results.Ok(result.Value);
        }).RequireAgencyAdmin();
    }
}
