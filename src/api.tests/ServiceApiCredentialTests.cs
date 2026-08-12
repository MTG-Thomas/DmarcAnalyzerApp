using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ServiceApiCredentialTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    [Fact]
    public async Task IssueRevealsOnceAndAuthenticatorAcceptsOnlyActiveToken()
    {
        await using var db = NewDb();
        var service = new ServiceApiCredentialService(db);
        var issued = (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", null, [ServiceApiPermissions.PortfolioRead]), default)).Value!;

        Assert.StartsWith("dmarc_api_v1.", issued.Token, StringComparison.Ordinal);
        var stored = await db.ServiceApiCredentials.SingleAsync();
        Assert.DoesNotContain(issued.Token, Convert.ToBase64String(stored.TokenHash), StringComparison.Ordinal);
        Assert.Equal(32, stored.TokenHash.Length);

        var authenticator = new ServiceApiAuthenticator(db);
        var principal = await authenticator.AuthenticateAsync(issued.Token, default);
        Assert.Equal(issued.Id, principal!.CredentialId);
        Assert.Equal("Bifrost", principal.Name);

        await service.RevokeAsync(issued.Id, default);
        Assert.Null(await authenticator.AuthenticateAsync(issued.Token, default));
    }

    [Fact]
    public async Task InvalidMalformedExpiredAndWrongTokensAreUniformlyRejected()
    {
        await using var db = NewDb();
        var service = new ServiceApiCredentialService(db);
        var issued = (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", null, [ServiceApiPermissions.PortfolioRead]), default)).Value!;
        var credential = await db.ServiceApiCredentials.SingleAsync();
        credential.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        var authenticator = new ServiceApiAuthenticator(db);
        Assert.Null(await authenticator.AuthenticateAsync(null, default));
        Assert.Null(await authenticator.AuthenticateAsync("not-a-token", default));
        Assert.Null(await authenticator.AuthenticateAsync(
            issued.Token[..^1] + (issued.Token[^1] == 'A' ? 'B' : 'A'), default));
        Assert.Null(await authenticator.AuthenticateAsync(issued.Token, default));
    }

    [Theory]
    [InlineData("users.manage")]
    [InlineData("audit.read,portfolio.read")]
    public async Task AuthenticatorRejectsUnknownOrNoncanonicalStoredPermissions(string stored)
    {
        await using var db = NewDb();
        var service = new ServiceApiCredentialService(db);
        var issued = (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", null, [ServiceApiPermissions.PortfolioRead]), default)).Value!;
        var credential = await db.ServiceApiCredentials.SingleAsync();
        credential.Permissions = stored.Split(',');
        await db.SaveChangesAsync();

        Assert.Null(await new ServiceApiAuthenticator(db).AuthenticateAsync(issued.Token, default));
    }

    [Fact]
    public async Task IssueValidatesNameAndBoundedExpiry()
    {
        await using var db = NewDb();
        var service = new ServiceApiCredentialService(db);

        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest(" ", null, [ServiceApiPermissions.PortfolioRead]), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost\nforged", null, [ServiceApiPermissions.PortfolioRead]), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", DateTime.UtcNow.AddYears(2), [ServiceApiPermissions.PortfolioRead]), default)).StatusCode);
        Assert.Empty(db.ServiceApiCredentials);
    }

    [Fact]
    public async Task IssueRequiresKnownPermissionsAndNormalizesCatalogOrder()
    {
        await using var db = NewDb();
        var service = new ServiceApiCredentialService(db);

        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", null, null), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", null, []), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", null, ["users.manage"]), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", null,
                [ServiceApiPermissions.PortfolioRead, ServiceApiPermissions.PortfolioRead]), default)).StatusCode);

        var issued = (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", null,
                [ServiceApiPermissions.AuditRead, ServiceApiPermissions.PortfolioRead]),
            default)).Value!;
        Assert.Equal([ServiceApiPermissions.PortfolioRead, ServiceApiPermissions.AuditRead], issued.Permissions);
    }

    [Fact]
    public async Task ListAndRevokeExposeMetadataAndRevocationIsIdempotent()
    {
        await using var db = NewDb();
        var service = new ServiceApiCredentialService(db);
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var issued = (await service.IssueAsync(
            new CreateServiceApiCredentialRequest(" Bifrost ", expiry, [ServiceApiPermissions.AuditRead, ServiceApiPermissions.PortfolioRead]), default)).Value!;

        var listed = Assert.Single(await service.ListAsync(default));
        Assert.Equal("Bifrost", listed.Name);
        Assert.Equal([ServiceApiPermissions.PortfolioRead, ServiceApiPermissions.AuditRead], listed.Permissions);
        Assert.Equal(expiry.UtcDateTime, listed.ExpiresAtUtc, TimeSpan.FromSeconds(1));

        var first = (await service.RevokeAsync(issued.Id, default)).Value!;
        var second = (await service.RevokeAsync(issued.Id, default)).Value!;
        Assert.NotNull(first.RevokedAtUtc);
        Assert.Equal(first.RevokedAtUtc, second.RevokedAtUtc);
        Assert.Equal(404, (await service.RevokeAsync(Guid.NewGuid(), default)).StatusCode);
    }

    [Fact]
    public async Task BearerServiceTokenAuthenticatesAsGlobalAnalyst()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/clients";
        context.Request.Headers.Authorization = "Bearer valid";
        context.Request.Headers.Cookie = "dmarc_session=must-not-be-used";
        var current = new CurrentUserContext();
        var reachedEndpoint = false;
        var middleware = new SessionAuthMiddleware(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            context,
            new ThrowingAuthService(),
            new StubServiceAuthenticator(new ServiceApiPrincipal(Guid.NewGuid(), "Bifrost", [ServiceApiPermissions.PortfolioRead])),
            current);

        Assert.True(reachedEndpoint);
        Assert.True(current.IsAuthenticated);
        Assert.Equal("service", current.ActorType);
        Assert.Equal("service:Bifrost", current.Email);
        Assert.Equal(Roles.AgencyAnalyst, current.Role);
        Assert.True(current.CanAccessClient(Guid.NewGuid()));
        Assert.False(current.IsAdmin);
    }

    [Fact]
    public async Task InvalidAuthorizationNeverFallsBackToCookieSession()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/clients";
        context.Request.Headers.Authorization = "Bearer invalid";
        context.Request.Headers.Cookie = "dmarc_session=valid-looking-cookie";
        var reachedEndpoint = false;
        var middleware = new SessionAuthMiddleware(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            context,
            new ThrowingAuthService(),
            new StubServiceAuthenticator(null),
            new CurrentUserContext());

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(reachedEndpoint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("expired")]
    public async Task MissingOrInvalidCookieIsRejected(string? cookieId)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/clients";
        if (cookieId is not null)
        {
            context.Request.Headers.Cookie = $"dmarc_session={cookieId}";
        }

        var reachedEndpoint = false;
        var middleware = new SessionAuthMiddleware(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            context,
            new ThrowingAuthService(returnNullSession: true),
            new StubServiceAuthenticator(null),
            new CurrentUserContext());

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(reachedEndpoint);
    }

    [Fact]
    public async Task ServiceTokenCannotReachAdminEndpoints()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/users";
        context.Request.Headers.Authorization = "Bearer valid";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(
                new RoleRequirementMetadata(RoleRequirement.AgencyAdmin)),
            "admin endpoint"));
        var current = new CurrentUserContext();
        var reachedEndpoint = false;
        var roleMiddleware = new RoleAuthorizationMiddleware(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });
        var sessionMiddleware = new SessionAuthMiddleware(nextContext =>
            roleMiddleware.InvokeAsync(nextContext, current));

        await sessionMiddleware.InvokeAsync(
            context,
            new ThrowingAuthService(),
            new StubServiceAuthenticator(new ServiceApiPrincipal(Guid.NewGuid(), "Bifrost", [ServiceApiPermissions.PortfolioRead])),
            current);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(reachedEndpoint);
    }

    [Fact]
    public async Task ServiceTokenRequiresExplicitEndpointPermission()
    {
        var current = new TestCurrentUserContext
        {
            ActorType = "service",
            Role = Roles.AgencyAnalyst,
            ServicePermissions = [ServiceApiPermissions.PortfolioRead],
        };

        Assert.Equal(200, await AuthorizeAsync(
            current,
            new ServicePermissionMetadata(ServiceApiPermissions.PortfolioRead)));
        Assert.Equal(403, await AuthorizeAsync(
            current,
            new ServicePermissionMetadata(ServiceApiPermissions.ClientsManage)));
        Assert.Equal(403, await AuthorizeAsync(current));
        Assert.Equal(403, await AuthorizeAsync(
            current,
            new RoleRequirementMetadata(RoleRequirement.AnyAuthenticated)));

        var adminHuman = TestCurrentUserContext.Admin();
        Assert.Equal(200, await AuthorizeAsync(adminHuman,
            new RoleRequirementMetadata(RoleRequirement.AgencyAdmin)));
        Assert.Equal(200, await AuthorizeAsync(adminHuman,
            new ServicePermissionMetadata(ServiceApiPermissions.PortfolioRead)));
    }

    [Theory]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/service-credentials")]
    [InlineData("/api/v1/admin/config/export")]
    [InlineData("/api/v1/admin/database/migrate")]
    [InlineData("/api/v1/auth/me")]
    public async Task ServiceTokenCannotUseUnscopedSensitiveEndpoint(string path)
    {
        var current = new TestCurrentUserContext
        {
            ActorType = "service",
            Role = Roles.AgencyAnalyst,
            ServicePermissions = ServiceApiPermissions.Catalog.Select(x => x.Id).ToArray(),
        };

        Assert.Equal(403, await AuthorizeAsync(current, path: path));
    }

    private static async Task<int> AuthorizeAsync(
        ICurrentUserContext current,
        object? metadata = null,
        string path = "/api/v1/test")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            metadata is null ? new EndpointMetadataCollection() : new EndpointMetadataCollection(metadata),
            "test"));
        context.Response.StatusCode = 200;
        await new RoleAuthorizationMiddleware(_ => Task.CompletedTask).InvokeAsync(context, current);
        return context.Response.StatusCode;
    }

    private sealed class StubServiceAuthenticator(ServiceApiPrincipal? principal) : IServiceApiAuthenticator
    {
        public Task<ServiceApiPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct)
            => Task.FromResult(principal);
    }

    private sealed class ThrowingAuthService(bool returnNullSession = false) : IAuthService
    {
        private static Exception Unexpected() => new InvalidOperationException("cookie session must not run");
        public Task<bool> RequiresBootstrapAsync(CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<UserDto>> RegisterAsync(RegisterRequest request, CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<LoginResultDto>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<LoginResultDto>> LoginWithExternalIdentityAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct) => throw Unexpected();
        public Task LogoutAsync(string cookieId, CancellationToken ct) => throw Unexpected();
        public Task<UserDto?> GetCurrentUserAsync(string cookieId, CancellationToken ct) => throw Unexpected();
        public Task<SessionUserDto?> GetSessionUserAsync(string cookieId, CancellationToken ct)
            => returnNullSession ? Task.FromResult<SessionUserDto?>(null) : throw Unexpected();
    }
}
