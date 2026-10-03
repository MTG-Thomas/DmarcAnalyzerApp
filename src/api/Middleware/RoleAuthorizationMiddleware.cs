using DmarcAnalyzer.Api.Application.Auth;

namespace DmarcAnalyzer.Api.Middleware;

/// <summary>
/// Enforces endpoint role requirements after SessionAuthMiddleware has
/// authenticated the request. Endpoints without RoleRequirementMetadata
/// default to agency staff, so client_viewer is deny-by-default: new
/// endpoints must opt in via AllowClientViewer() to be visible to viewers.
/// </summary>
public sealed class RoleAuthorizationMiddleware(RequestDelegate next)
{
    /// <summary>Enforces the endpoint's role or service-permission requirement for one request.</summary>
    public async Task InvokeAsync(HttpContext context, ICurrentUserContext currentUser)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        // Public and non-API paths were already passed through by SessionAuthMiddleware.
        if (!path.StartsWith("/api/v1/") || !currentUser.IsAuthenticated)
        {
            await next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        if (currentUser.IsService)
        {
            var permission = endpoint?.Metadata.GetMetadata<ServicePermissionMetadata>()?.Permission;
            if (permission is null || !currentUser.HasServicePermission(permission))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(
                    new { error = "forbidden" },
                    context.RequestAborted);
                return;
            }

            await next(context);
            return;
        }

        if (currentUser.IsMagicLink)
        {
            // Read-only, single-client, explicit opt-in: a magic link reaches a
            // GET endpoint only when that endpoint carries both AnyAuthenticated
            // (the client_viewer read surface) and MagicLinkAllowed. Everything
            // else — writes, staff/admin endpoints, passkeys, and any future
            // endpoint that forgot the marker — is 403.
            var magicAllowed = HttpMethods.IsGet(context.Request.Method)
                || HttpMethods.IsHead(context.Request.Method);
            magicAllowed = magicAllowed
                && endpoint?.Metadata.GetMetadata<RoleRequirementMetadata>()?.Requirement
                    == RoleRequirement.AnyAuthenticated
                && endpoint?.Metadata.GetMetadata<MagicLinkAllowedMetadata>() is not null;
            if (!magicAllowed)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(
                    new { error = "forbidden" },
                    context.RequestAborted);
                return;
            }

            await next(context);
            return;
        }

        var requirement = endpoint?.Metadata
            .GetMetadata<RoleRequirementMetadata>()?.Requirement
            ?? RoleRequirement.AgencyStaff;

        var allowed = requirement switch
        {
            RoleRequirement.AgencyAdmin => currentUser.IsAdmin,
            RoleRequirement.AgencyStaff => currentUser.IsAgencyStaff,
            RoleRequirement.AnyAuthenticated => true,
            _ => false,
        };

        if (!allowed)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "forbidden" });
            return;
        }

        await next(context);
    }
}
