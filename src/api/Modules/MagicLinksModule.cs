using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.MagicLinks;

namespace DmarcAnalyzer.Api.Modules;

public sealed class MagicLinksModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/magic-links", async (
            Guid? clientId,
            IMagicLinkService service,
            CancellationToken ct) => Results.Ok(await service.ListAsync(clientId, ct)))
            .RequireAgencyAdmin();

        app.MapPost("/api/v1/magic-links", async (
            CreateMagicLinkRequest request,
            IMagicLinkService service,
            IAuditLog audit,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await service.IssueAsync(request, ct);
            if (!result.IsSuccess)
            {
                return result.StatusCode == 404
                    ? Results.NotFound()
                    : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var issued = result.Value!;
            await audit.RecordAsync(
                AuditEvents.MagicLinkCreated,
                $"Issued magic link {issued.Label} for client",
                "magic_link",
                issued.Id,
                issued.ClientId,
                ct: ct);
            http.Response.Headers.CacheControl = "no-store";
            var url = $"/client-view?token={Uri.EscapeDataString(issued.Token)}";
            return Results.Created(
                $"/api/v1/magic-links/{issued.Id}",
                new
                {
                    issued.Id,
                    issued.ClientId,
                    issued.Label,
                    issued.Prefix,
                    issued.Token,
                    Url = url,
                    issued.CreatedAtUtc,
                    issued.ExpiresAtUtc,
                });
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/magic-links/{id:guid}/revoke", async (
            Guid id,
            IMagicLinkService service,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var result = await service.RevokeAsync(id, ct);
            if (!result.IsSuccess)
            {
                return Results.NotFound();
            }

            await audit.RecordAsync(
                AuditEvents.MagicLinkRevoked,
                $"Revoked magic link {result.Value!.Label}",
                "magic_link",
                id,
                result.Value.ClientId,
                ct: ct);
            return Results.Ok(result.Value);
        }).RequireAgencyAdmin();
    }
}
