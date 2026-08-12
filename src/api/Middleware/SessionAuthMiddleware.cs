using DmarcAnalyzer.Api.Application.Auth;
using AuthenticationHeaderValue = System.Net.Http.Headers.AuthenticationHeaderValue;

namespace DmarcAnalyzer.Api.Middleware;

public sealed class SessionAuthMiddleware(RequestDelegate next)
{
    private const string CookieName = "dmarc_session";

    private static readonly HashSet<string> PublicPaths =
    [
        "/api/v1/auth/login",
        "/api/v1/auth/register",
        "/api/v1/auth/logout",
        "/api/v1/auth/setup",
        "/api/v1/auth/providers",
        "/health/live",
        "/health/ready",
    ];

    // OIDC challenge/callback/completion endpoints authenticate via the
    // external-temp scheme, not an app session.
    private const string OidcPathPrefix = "/api/v1/auth/oidc/";

    public async Task InvokeAsync(
        HttpContext context,
        IAuthService authService,
        IServiceApiAuthenticator serviceApiAuthenticator,
        CurrentUserContext currentUserContext)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        if (!path.StartsWith("/api/v1/") || PublicPaths.Contains(path) || path.StartsWith(OidcPathPrefix))
        {
            await next(context);
            return;
        }

        if (context.Request.Headers.Authorization.Count > 0)
        {
            var token = GetBearerToken(context.Request);
            var principal = await serviceApiAuthenticator.AuthenticateAsync(
                token,
                context.RequestAborted);
            if (principal is null)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(
                    new { error = "not authenticated" },
                    context.RequestAborted);
                return;
            }

            currentUserContext.SetService(principal);
            await next(context);
            return;
        }

        var cookieId = context.Request.Cookies[CookieName];
        if (cookieId is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(
                new { error = "not authenticated" },
                context.RequestAborted);
            return;
        }

        var sessionUser = await authService.GetSessionUserAsync(cookieId, context.RequestAborted);
        if (sessionUser is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(
                new { error = "session expired or invalid" },
                context.RequestAborted);
            return;
        }

        currentUserContext.Set(sessionUser.User, sessionUser.GrantedClientIds);
        await next(context);
    }

    private static string? GetBearerToken(HttpRequest request)
    {
        if (request.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
        {
            return null;
        }

        return header.Parameter;
    }
}
