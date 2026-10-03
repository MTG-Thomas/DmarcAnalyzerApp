using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.MagicLinks;
using DmarcAnalyzer.Api.Contracts.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Middleware;
using DmarcAnalyzer.Api.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class MagicLinkTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static async Task<Client> SeedClientAsync(DmarcAnalyzerDbContext db, string slug)
    {
        var client = new Client { Name = slug, Slug = slug, Timezone = "UTC" };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    [Fact]
    public async Task IssuesRevealOnceTokenWithSevenDayDefaultExpiry()
    {
        await using var db = NewDb();
        var client = await SeedClientAsync(db, "acme");

        var before = DateTime.UtcNow;
        var result = await new MagicLinkService(db).IssueAsync(
            new CreateMagicLinkRequest(client.Id, null, "April review"), default);

        Assert.True(result.IsSuccess);
        var issued = result.Value!;
        Assert.Matches("^dmarc_ml_v1\\.[A-Za-z0-9_-]{22}\\.[A-Za-z0-9_-]{43}$", issued.Token);
        Assert.Equal(22, issued.Prefix.Length);
        Assert.InRange(
            issued.ExpiresAtUtc,
            before.AddDays(7).AddMinutes(-1),
            DateTime.UtcNow.AddDays(7).AddMinutes(1));

        var stored = await db.MagicLinks.SingleAsync();
        Assert.Equal(issued.Prefix, stored.Prefix);
        Assert.Equal(32, stored.TokenHash.Length);
        Assert.Null(stored.RevokedAtUtc);
        Assert.Null(stored.LastUsedAtUtc);
        Assert.DoesNotContain(
            typeof(MagicLink).GetProperties(),
            property => property.Name.Contains("Token", StringComparison.Ordinal)
                        && property.Name != nameof(MagicLink.TokenHash));
    }

    [Fact]
    public async Task IssueValidatesClientLabelAndExpiryBounds()
    {
        await using var db = NewDb();
        var client = await SeedClientAsync(db, "acme");
        var service = new MagicLinkService(db);

        Assert.Equal(404, (await service.IssueAsync(
            new CreateMagicLinkRequest(Guid.NewGuid(), null, "Review"), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateMagicLinkRequest(client.Id, null, " "), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateMagicLinkRequest(client.Id, null, new string('x', 101)), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateMagicLinkRequest(client.Id, 0, "Review"), default)).StatusCode);
        Assert.Equal(400, (await service.IssueAsync(
            new CreateMagicLinkRequest(client.Id, 31, "Review"), default)).StatusCode);

        var custom = (await service.IssueAsync(
            new CreateMagicLinkRequest(client.Id, 1, "One day"), default)).Value!;
        Assert.InRange(
            custom.ExpiresAtUtc,
            DateTime.UtcNow.AddDays(1).AddMinutes(-1),
            DateTime.UtcNow.AddDays(1).AddMinutes(1));
        Assert.Empty(await db.MagicLinks.Where(x => x.Label == " ").ToListAsync());
    }

    [Fact]
    public async Task AuthenticatorAcceptsOnlyActiveUnexpiredLink()
    {
        await using var db = NewDb();
        var client = await SeedClientAsync(db, "acme");
        var service = new MagicLinkService(db);
        var issued = (await service.IssueAsync(
            new CreateMagicLinkRequest(client.Id, 7, "Review"), default)).Value!;
        var authenticator = new MagicLinkAuthenticator(db);

        var principal = await authenticator.AuthenticateAsync(issued.Token, default);
        Assert.NotNull(principal);
        Assert.Equal(issued.Id, principal.MagicLinkId);
        Assert.Equal(client.Id, principal.ClientId);

        Assert.Null(await authenticator.AuthenticateAsync(null, default));
        Assert.Null(await authenticator.AuthenticateAsync("not-a-token", default));
        var tampered = issued.Token[..^1] + (issued.Token[^1] == 'A' ? 'B' : 'A');
        Assert.Null(await authenticator.AuthenticateAsync(tampered, default));

        // A service token must not authenticate as a magic link and vice versa.
        Assert.Null(await authenticator.AuthenticateAsync(
            issued.Token.Replace("dmarc_ml_v1", "dmarc_api_v1", StringComparison.Ordinal), default));

        var stored = await db.MagicLinks.SingleAsync();
        stored.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.Null(await authenticator.AuthenticateAsync(issued.Token, default));

        stored.ExpiresAtUtc = DateTime.UtcNow.AddDays(7);
        stored.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.Null(await authenticator.AuthenticateAsync(issued.Token, default));
    }

    [Fact]
    public async Task ListFiltersByClientAndRevokeIsIdempotent()
    {
        await using var db = NewDb();
        var first = await SeedClientAsync(db, "acme");
        var second = await SeedClientAsync(db, "globex");
        var service = new MagicLinkService(db);
        var issued = (await service.IssueAsync(
            new CreateMagicLinkRequest(first.Id, 7, "Review"), default)).Value!;
        await service.IssueAsync(new CreateMagicLinkRequest(second.Id, 7, "Other"), default);

        Assert.Equal(2, (await service.ListAsync(null, default)).Count);
        var filtered = await service.ListAsync(first.Id, default);
        var only = Assert.Single(filtered);
        Assert.Equal(first.Id, only.ClientId);
        Assert.Equal("acme", only.ClientName);
        Assert.Equal(issued.Prefix, only.Prefix);

        var revoked = (await service.RevokeAsync(issued.Id, default)).Value!;
        Assert.NotNull(revoked.RevokedAtUtc);
        var again = (await service.RevokeAsync(issued.Id, default)).Value!;
        Assert.Equal(revoked.RevokedAtUtc, again.RevokedAtUtc);
        Assert.Equal(404, (await service.RevokeAsync(Guid.NewGuid(), default)).StatusCode);

        Assert.Null(await new MagicLinkAuthenticator(db).AuthenticateAsync(issued.Token, default));
    }

    [Fact]
    public async Task TouchLastUsedRecordsUsageWithoutBreakingReads()
    {
        await using var db = NewDb();
        var client = await SeedClientAsync(db, "acme");
        var issued = (await new MagicLinkService(db).IssueAsync(
            new CreateMagicLinkRequest(client.Id, 7, "Review"), default)).Value!;
        var authenticator = new MagicLinkAuthenticator(db);

        Assert.Null((await db.MagicLinks.SingleAsync()).LastUsedAtUtc);
        await authenticator.TouchLastUsedAsync(issued.Id, default);
        Assert.NotNull((await db.MagicLinks.SingleAsync()).LastUsedAtUtc);

        // Unknown ids are ignored — usage telemetry must not throw.
        await authenticator.TouchLastUsedAsync(Guid.NewGuid(), default);
    }

    [Fact]
    public async Task MagicLinkBearerAuthenticatesToSingleClientContext()
    {
        await using var db = NewDb();
        var client = await SeedClientAsync(db, "acme");
        var otherId = Guid.NewGuid();
        var issued = (await new MagicLinkService(db).IssueAsync(
            new CreateMagicLinkRequest(client.Id, 7, "Review"), default)).Value!;

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/analytics/summary";
        context.Request.Headers.Authorization = $"Bearer {issued.Token}";
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
            new StubServiceAuthenticator(null),
            new MagicLinkAuthenticator(db),
            current,
            NullLogger<SessionAuthMiddleware>.Instance);

        Assert.True(reachedEndpoint);
        Assert.True(current.IsAuthenticated);
        Assert.True(current.IsMagicLink);
        Assert.Equal("magic_link", current.ActorType);
        Assert.Equal(issued.Id, current.MagicLinkId);
        Assert.False(current.IsAgencyStaff);
        Assert.False(current.IsService);
        Assert.True(current.CanAccessClient(client.Id));
        Assert.False(current.CanAccessClient(otherId));
        Assert.NotNull((await db.MagicLinks.SingleAsync()).LastUsedAtUtc);
    }

    [Fact]
    public async Task BadMagicLinkTokenIs401WithNoCookieFallback()
    {
        await using var db = NewDb();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/analytics/summary";
        context.Request.Headers.Authorization = "Bearer dmarc_ml_v1.badprefix.badsecret";
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
            new MagicLinkAuthenticator(db),
            new CurrentUserContext(),
            NullLogger<SessionAuthMiddleware>.Instance);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(reachedEndpoint);
    }

    [Theory]
    [InlineData("GET", true, true, 200)]
    [InlineData("HEAD", true, true, 200)]
    [InlineData("POST", true, true, 403)]
    [InlineData("GET", true, false, 403)]
    [InlineData("GET", false, true, 403)]
    public async Task MagicLinkAuthorizationIsReadOnlyAndOptIn(
        string method, bool anyAuthenticated, bool magicAllowed, int expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/analytics/summary";
        context.Request.Method = method;
        var metadata = new List<object>();
        if (anyAuthenticated)
        {
            metadata.Add(new RoleRequirementMetadata(RoleRequirement.AnyAuthenticated));
        }

        if (magicAllowed)
        {
            metadata.Add(new MagicLinkAllowedMetadata());
        }

        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection([.. metadata]),
            "test"));
        context.Response.StatusCode = 200;
        var current = new TestCurrentUserContext
        {
            ActorType = "magic_link",
            Role = Roles.ClientViewer,
            AllowedClientIds = [Guid.NewGuid()],
            MagicLinkId = Guid.NewGuid(),
        };

        await new RoleAuthorizationMiddleware(_ => Task.CompletedTask).InvokeAsync(context, current);

        Assert.Equal(expected, context.Response.StatusCode);
    }

    [Fact]
    public void MagicLinkAdminEndpointsAreAdminOnlyWithoutMagicMarkers()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        builder.Services.AddScoped<ICurrentUserContext>(_ => TestCurrentUserContext.Admin());
        builder.Services.AddScoped<IMagicLinkService>(_ => null!);
        builder.Services.AddScoped<IAuditLog>(_ => null!);
        var app = builder.Build();
        new MagicLinksModule().AddRoutes(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.Equal(3, endpoints.Length);
        Assert.All(endpoints, endpoint =>
        {
            Assert.Equal(
                RoleRequirement.AgencyAdmin,
                endpoint.Metadata.GetMetadata<RoleRequirementMetadata>()?.Requirement);
            Assert.Null(endpoint.Metadata.GetMetadata<MagicLinkAllowedMetadata>());
            Assert.Null(endpoint.Metadata.GetMetadata<ServicePermissionMetadata>());
        });
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
