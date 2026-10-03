using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.MagicLinks;
using AuthenticationHeaderValue = System.Net.Http.Headers.AuthenticationHeaderValue;

namespace DmarcAnalyzer.Api.Middleware;

/// <summary>
/// The cookie front door for /api/v1/*: resolves the dmarc_session cookie to a
/// user, populates <see cref="CurrentUserContext"/>, and 401s everything else.
/// Paths outside /api/v1/ (health, MTA-STS, the SPA) pass through untouched,
/// as do the listed public auth endpoints and Bearer-authenticated requests
/// (service credentials and magic links).
/// </summary>
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
        "/api/v1/auth/passkeys/options",
        "/api/v1/auth/passkeys/complete",
        "/health/live",
        "/health/ready",
    ];

    // OIDC challenge/callback/completion endpoints authenticate via the
    // external-temp scheme, not an app session.
    private const string OidcPathPrefix = "/api/v1/auth/oidc/";

    /// <summary>Runs the gate for one request.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        IAuthService authService,
        IServiceApiAuthenticator serviceApiAuthenticator,
        IMagicLinkAuthenticator magicLinkAuthenticator,
        CurrentUserContext currentUserContext,
        ILogger<SessionAuthMiddleware> logger)
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
            var servicePrincipal = await serviceApiAuthenticator.AuthenticateAsync(
                token,
                context.RequestAborted);
            if (servicePrincipal is not null)
            {
                currentUserContext.SetService(servicePrincipal);
                await next(context);
                return;
            }

            var magicLink = await magicLinkAuthenticator.AuthenticateAsync(
                token,
                context.RequestAborted);
            if (magicLink is null)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(
                    new { error = "not authenticated" },
                    context.RequestAborted);
                return;
            }

            currentUserContext.SetMagicLink(magicLink.MagicLinkId, magicLink.ClientId, magicLink.Label);

            // Best-effort usage evidence for the admin list. A failed write must
            // not turn a succeeding read into a 500.
            try
            {
                await magicLinkAuthenticator.TouchLastUsedAsync(
                    magicLink.MagicLinkId,
                    context.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to record magic-link usage");
            }

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
