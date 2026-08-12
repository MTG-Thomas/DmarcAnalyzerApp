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
            new CreateServiceApiCredentialRequest("Bifrost", null), default)).Value!;

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
            new CreateServiceApiCredentialRequest("Bifrost", null), default)).Value!;
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

    [Fact]
    public async Task IssueValidatesNameAndBoundedExpiry()
    {
        await using var db = NewDb();
        var service = new ServiceApiCredentialService(db);

        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest(" ", null), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost\nforged", null), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateServiceApiCredentialRequest("Bifrost", DateTime.UtcNow.AddYears(2)), default)).StatusCode);
        Assert.Empty(db.ServiceApiCredentials);
    }

    [Fact]
    public async Task ListAndRevokeExposeMetadataAndRevocationIsIdempotent()
    {
        await using var db = NewDb();
        var service = new ServiceApiCredentialService(db);
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var issued = (await service.IssueAsync(
            new CreateServiceApiCredentialRequest(" Bifrost ", expiry), default)).Value!;

        var listed = Assert.Single(await service.ListAsync(default));
        Assert.Equal("Bifrost", listed.Name);
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
            new StubServiceAuthenticator(new ServiceApiPrincipal(Guid.NewGuid(), "Bifrost")),
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
            new StubServiceAuthenticator(new ServiceApiPrincipal(Guid.NewGuid(), "Bifrost")),
            current);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(reachedEndpoint);
    }

    private sealed class StubServiceAuthenticator(ServiceApiPrincipal? principal) : IServiceApiAuthenticator
    {
        public Task<ServiceApiPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct)
            => Task.FromResult(principal);
    }

    private sealed class ThrowingAuthService : IAuthService
    {
        private static Exception Unexpected() => new InvalidOperationException("cookie session must not run");
        public Task<bool> RequiresBootstrapAsync(CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<UserDto>> RegisterAsync(RegisterRequest request, CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<LoginResultDto>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct) => throw Unexpected();
        public Task<ServiceResult<LoginResultDto>> LoginWithExternalIdentityAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct) => throw Unexpected();
        public Task LogoutAsync(string cookieId, CancellationToken ct) => throw Unexpected();
        public Task<UserDto?> GetCurrentUserAsync(string cookieId, CancellationToken ct) => throw Unexpected();
        public Task<SessionUserDto?> GetSessionUserAsync(string cookieId, CancellationToken ct) => throw Unexpected();
    }
}
