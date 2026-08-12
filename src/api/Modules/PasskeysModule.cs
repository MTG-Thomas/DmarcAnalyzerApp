using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using Fido2NetLib;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

namespace DmarcAnalyzer.Api.Modules;

public sealed class PasskeysModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/passkeys/options", (
            HttpContext http,
            IPasskeyService service,
            IOptions<PasskeyOptions> configured) =>
        {
            if (!configured.Value.Enabled) return Results.NotFound();
            if (!PasskeyRequestOrigin.IsAllowed(http.Request, configured.Value)) return ForbiddenOrigin();
            var result = service.AuthenticationOptions(http.Response, http.Request);
            return Results.Ok(result.Value);
        }).WithMetadata(new RequestSizeLimitAttribute(65_536)).RequireRateLimiting("passkey-anonymous");

        app.MapPost("/api/v1/auth/passkeys/complete", async (
            AuthenticatorAssertionRawResponse assertion,
            HttpContext http,
            IPasskeyService service,
            IAuditLog audit,
            IOptions<PasskeyOptions> configured,
            CancellationToken ct) =>
        {
            if (!configured.Value.Enabled) return Results.NotFound();
            if (!PasskeyRequestOrigin.IsAllowed(http.Request, configured.Value)) return ForbiddenOrigin();

            var result = await service.AuthenticateAsync(assertion, http.Request, http.Response, ct);
            if (!result.IsSuccess)
            {
                await audit.RecordAsync(AuditEvents.PasskeyLoginFailed, "Failed passkey sign-in", ct: ct);
                return Error(result.Error!, result.StatusCode);
            }

            var login = result.Value!;
            http.Response.Cookies.Append(SessionCookie.Name, login.CookieId, SessionCookie.Options(http.Request));
            await audit.RecordAsync(
                AuditEvents.PasskeyLoginSucceeded,
                $"Signed in with a passkey as {login.User.Email}",
                "user",
                login.User.Id,
                actorEmailOverride: login.User.Email,
                actorUserIdOverride: login.User.Id,
                ct: ct);
            return Results.Ok(new { user = login.User });
        }).WithMetadata(new RequestSizeLimitAttribute(65_536)).RequireRateLimiting("passkey-anonymous");

        app.MapGet("/api/v1/passkeys", async (
            IPasskeyService service,
            IOptions<PasskeyOptions> configured,
            CancellationToken ct) =>
        {
            if (!configured.Value.Enabled) return Results.NotFound();
            var result = await service.ListAsync(ct);
            return result.IsSuccess
                ? Results.Ok(new { passkeys = result.Value })
                : Error(result.Error!, result.StatusCode);
        }).AllowClientViewer();

        app.MapPost("/api/v1/passkeys/options", async (
            HttpContext http,
            IPasskeyService service,
            IOptions<PasskeyOptions> configured,
            CancellationToken ct) =>
        {
            if (!configured.Value.Enabled) return Results.NotFound();
            if (!PasskeyRequestOrigin.IsAllowed(http.Request, configured.Value)) return ForbiddenOrigin();
            var result = await service.RegistrationOptionsAsync(http.Request, http.Response, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Error(result.Error!, result.StatusCode);
        }).WithMetadata(new RequestSizeLimitAttribute(65_536)).AllowClientViewer().RequireRateLimiting("passkey-management");

        app.MapPost("/api/v1/passkeys", async (
            RegisterPasskeyRequest request,
            HttpContext http,
            IPasskeyService service,
            IAuditLog audit,
            IOptions<PasskeyOptions> configured,
            CancellationToken ct) =>
        {
            if (!configured.Value.Enabled) return Results.NotFound();
            if (!PasskeyRequestOrigin.IsAllowed(http.Request, configured.Value)) return ForbiddenOrigin();
            var result = await service.RegisterAsync(request, http.Request, http.Response, ct);
            if (!result.IsSuccess) return Error(result.Error!, result.StatusCode);
            await audit.RecordAsync(AuditEvents.PasskeyRegistered, "Registered a passkey", "passkey", result.Value!.Id, ct: ct);
            return Results.Created($"/api/v1/passkeys/{result.Value.Id}", result.Value);
        }).WithMetadata(new RequestSizeLimitAttribute(65_536)).AllowClientViewer().RequireRateLimiting("passkey-management");

        app.MapPut("/api/v1/passkeys/{id:guid}", async (
            Guid id,
            RenamePasskeyRequest request,
            HttpContext http,
            IPasskeyService service,
            IAuditLog audit,
            IOptions<PasskeyOptions> configured,
            CancellationToken ct) =>
        {
            if (!configured.Value.Enabled) return Results.NotFound();
            if (!PasskeyRequestOrigin.IsAllowed(http.Request, configured.Value)) return ForbiddenOrigin();
            var result = await service.RenameAsync(id, request, http.Request, ct);
            if (!result.IsSuccess) return Error(result.Error!, result.StatusCode);
            await audit.RecordAsync(AuditEvents.PasskeyRenamed, "Renamed a passkey", "passkey", id, ct: ct);
            return Results.Ok(result.Value);
        }).WithMetadata(new RequestSizeLimitAttribute(65_536)).AllowClientViewer().RequireRateLimiting("passkey-management");

        app.MapDelete("/api/v1/passkeys/{id:guid}", async (
            Guid id,
            HttpContext http,
            IPasskeyService service,
            IAuditLog audit,
            IOptions<PasskeyOptions> configured,
            CancellationToken ct) =>
        {
            if (!configured.Value.Enabled) return Results.NotFound();
            if (!PasskeyRequestOrigin.IsAllowed(http.Request, configured.Value)) return ForbiddenOrigin();
            var result = await service.RemoveAsync(id, http.Request, ct);
            if (!result.IsSuccess) return Error(result.Error!, result.StatusCode);
            await audit.RecordAsync(AuditEvents.PasskeyRemoved, "Removed a passkey", "passkey", id, ct: ct);
            return Results.NoContent();
        }).AllowClientViewer().RequireRateLimiting("passkey-management");
    }

    private static IResult ForbiddenOrigin() => Error("request origin is not allowed", 403);
    private static IResult Error(string error, int statusCode) => Results.Json(new { error }, statusCode: statusCode);
}
