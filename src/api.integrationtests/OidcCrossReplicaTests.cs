using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// Handler-level proof that OIDC login roams across replicas: the challenge's
/// correlation/nonce state and the external-temp cookie are all DataProtection
/// payloads, so a login started on one host must complete on another when both
/// share the durable <c>dp_key</c> ring — and must fail when the callback host
/// holds a different ring.
/// </summary>
[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class OidcCrossReplicaTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task ChallengeOnA_CallbackOnB_CompleteOnA_MintsSession()
    {
        GuardAmbientMode();
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using var idp = new StubOidcProvider();
        await idp.StartAsync();

        using var env = TemporaryEnvironment.Create(database.ConnectionString, idp.Authority);
        using var replicaA = new ReplicaFactory();
        using var replicaB = new ReplicaFactory();
        using var clientA = replicaA.CreateClient(NoCookiesNoRedirects());
        using var clientB = replicaB.CreateClient(NoCookiesNoRedirects());

        // 1. Challenge on A: 302 to the provider plus correlation/nonce cookies.
        using var login = await clientA.GetAsync("/api/v1/auth/oidc/login?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authorizeUrl = Assert.IsType<Uri>(login.Headers.Location);
        Assert.StartsWith(idp.Authority + "/authorize?", authorizeUrl.ToString());
        var challengeCookies = login.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(challengeCookies, c => c.Contains(".AspNetCore.Correlation.", StringComparison.Ordinal));
        Assert.Contains(challengeCookies, c => c.Contains(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal));

        // 2. Follow the provider leg over real HTTP; the stub auto-approves and
        //    redirects back to the callback URL replica A registered.
        using var idpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var approval = await idpClient.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, approval.StatusCode);
        var callbackPath = Assert.IsType<Uri>(approval.Headers.Location).PathAndQuery;

        // 3. Replay that callback on B with A's cookies — the cross-replica hop.
        using var callback = await clientB.SendAsync(CookieGet(callbackPath, challengeCookies));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var completeUrl = Assert.IsType<Uri>(callback.Headers.Location).ToString();
        Assert.StartsWith("/api/v1/auth/oidc/complete", completeUrl, StringComparison.Ordinal);
        var tempCookies = callback.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(tempCookies, c => c.StartsWith("dmarc_ext=", StringComparison.Ordinal));

        // 4. Complete back on A: proves the temp cookie roams too, B -> A.
        using var complete = await clientA.SendAsync(CookieGet(completeUrl, tempCookies));
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        var sessionId = complete.Headers.GetValues("Set-Cookie")
            .Select(v => v.Split(';', 2)[0].Trim())
            .FirstOrDefault(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal))?
            .Substring((SessionCookie.Name + "=").Length);
        Assert.False(string.IsNullOrEmpty(sessionId));

        // 5. The minted session is real.
        using var me = await clientA.SendAsync(CookieGet("/api/v1/auth/me", [$"{SessionCookie.Name}={sessionId}"]));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(StubOidcProvider.Email, await me.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // 6. And the durable ring did the work — not an ephemeral fallback.
        await using var db = database.CreateDbContext();
        Assert.True(await db.Set<DpKey>().CountAsync() >= 1);
    }

    [Fact]
    public async Task CallbackOnFreshRing_RejectsCorrelation()
    {
        GuardAmbientMode();
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using var idp = new StubOidcProvider();
        await idp.StartAsync();

        using var env = TemporaryEnvironment.Create(database.ConnectionString, idp.Authority);
        using var replicaA = new ReplicaFactory();
        using var clientA = replicaA.CreateClient(NoCookiesNoRedirects());

        using var login = await clientA.GetAsync("/api/v1/auth/oidc/login?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authorizeUrl = Assert.IsType<Uri>(login.Headers.Location);
        var challengeCookies = login.Headers.GetValues("Set-Cookie").ToArray();

        using var idpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var approval = await idpClient.GetAsync(authorizeUrl);
        var callbackPath = Assert.IsType<Uri>(approval.Headers.Location).PathAndQuery;

        // A second database: same schema, a ring this challenge never saw.
        var stranger = new PostgreSqlDatabaseFixture();
        await stranger.InitializeAsync();
        try
        {
            await stranger.MigrateToLatestAsync();
            using var strangerEnv = TemporaryEnvironment.Create(stranger.ConnectionString, idp.Authority);
            using var replicaC = new ReplicaFactory();
            using var clientC = replicaC.CreateClient(NoCookiesNoRedirects());

            // TestServer rethrows server exceptions rather than answering 500:
            // the failure must name the unprotect step, proving ring-sharing
            // is the mechanism the positive test relies on.
            var failure = await Assert.ThrowsAsync<AuthenticationFailureException>(
                () => clientC.SendAsync(CookieGet(callbackPath, challengeCookies)));
            Assert.Contains("Unable to unprotect", failure.InnerException?.Message ?? failure.Message);
        }
        finally
        {
            await stranger.DisposeAsync();
        }
    }

    private static WebApplicationFactoryClientOptions NoCookiesNoRedirects() => new()
    {
        HandleCookies = false,
        AllowAutoRedirect = false,
    };

    private static HttpRequestMessage CookieGet(string path, IEnumerable<string> setCookieValues)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", string.Join("; ", setCookieValues.Select(v => v.Split(';', 2)[0].Trim())));
        return request;
    }

    private static void GuardAmbientMode()
    {
        var ambient = Environment.GetEnvironmentVariable("APP_MODE");
        Assert.True(
            string.IsNullOrWhiteSpace(ambient) || ambient.Equals("api", StringComparison.OrdinalIgnoreCase),
            $"These tests boot the API host; unset APP_MODE first (ambient value '{ambient}' selects a non-HTTP host).");
    }

    /// <summary>One API replica: the real <c>Program</c> wiring, configured through <see cref="TemporaryEnvironment"/>.</summary>
    private sealed class ReplicaFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
        }
    }

    /// <summary>
    /// Process environment for one replica build, restored on dispose (nestable).
    /// <para>
    /// This exists because <c>ConfigureWebHost</c> configuration lands in host
    /// config, which <c>Program</c> only sees merged at <c>Build()</c> — after
    /// the registration-time reads (<c>AddOidcAuthentication</c>) that decide
    /// whether auth services exist at all. Only ambient providers (environment
    /// variables here) are visible to both registration and the pipeline, so
    /// only they can carry this test's config. The keys are app-specific and no
    /// other test in this assembly boots <c>Program</c>, so a parallel
    /// collection cannot observe the window.
    /// </para>
    /// </summary>
    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly List<(string Key, string? Previous)> _previous = [];

        private TemporaryEnvironment() { }

        public static TemporaryEnvironment Create(string connectionString, string authority)
        {
            var scope = new TemporaryEnvironment();
            scope.Set("ConnectionStrings__Default", connectionString);
            scope.Set("DATABASE_URL", null);
            scope.Set("Database__MigrateOnStartup", "false");
            scope.Set("Auth__Oidc__Enabled", "true");
            scope.Set("Auth__Oidc__Authority", authority);
            scope.Set("Auth__Oidc__ClientId", StubOidcProvider.ClientId);
            scope.Set("Auth__Oidc__ClientSecret", "stub-secret");
            scope.Set("Auth__Oidc__RequireHttpsMetadata", "false");
            scope.Set("Auth__Oidc__AutoProvision", "true");
            return scope;
        }

        public void Dispose()
        {
            for (var i = _previous.Count - 1; i >= 0; i--)
            {
                Environment.SetEnvironmentVariable(_previous[i].Key, _previous[i].Previous);
            }

            _previous.Clear();
        }

        private void Set(string key, string? value)
        {
            _previous.Add((key, Environment.GetEnvironmentVariable(key)));
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// A minimal auto-approving OIDC provider over real HTTP: discovery, JWKS,
    /// authorize, token, userinfo. It echoes the authorize request's nonce into
    /// the id_token (looked up by code) and ignores PKCE verification and token
    /// endpoint auth — server-side strictness is not what these tests prove.
    /// </summary>
    private sealed class StubOidcProvider : IAsyncDisposable
    {
        public const string ClientId = "stub-client";
        public const string Email = "sso-user@example.test";

        private const string Subject = "stub-subject-1";
        private const string KeyId = "stub-key";

        private readonly RSA _rsa = RSA.Create(2048);
        private readonly ConcurrentDictionary<string, string?> _codes = new();
        private readonly WebApplication _app;

        public StubOidcProvider()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            _app = builder.Build();

            _app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object?>
            {
                ["issuer"] = Authority,
                ["authorization_endpoint"] = Authority + "/authorize",
                ["token_endpoint"] = Authority + "/token",
                ["userinfo_endpoint"] = Authority + "/userinfo",
                ["jwks_uri"] = Authority + "/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            }));

            _app.MapGet("/jwks", () =>
            {
                var parameters = _rsa.ExportParameters(false);
                return Results.Json(new Dictionary<string, object?>
                {
                    ["keys"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["kty"] = "RSA",
                            ["use"] = "sig",
                            ["kid"] = KeyId,
                            ["n"] = Base64Url(parameters.Modulus!),
                            ["e"] = Base64Url(parameters.Exponent!),
                        },
                    },
                });
            });

            _app.MapGet("/authorize", (HttpContext context) =>
            {
                var query = context.Request.Query;
                var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                _codes[code] = query.TryGetValue("nonce", out var nonce) ? nonce.ToString() : null;
                return Results.Redirect($"{query["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(query["state"].ToString())}");
            });

            _app.MapPost("/token", async (HttpContext context) =>
            {
                var form = await context.Request.ReadFormAsync();
                if (!_codes.TryRemove(form["code"].ToString(), out var nonce))
                {
                    return Results.BadRequest(new { error = "invalid_grant" });
                }

                return Results.Json(new Dictionary<string, object?>
                {
                    ["id_token"] = MintIdToken(nonce),
                    ["access_token"] = "stub-access-token",
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 300,
                });
            });

            _app.MapGet("/userinfo", () => Results.Json(new Dictionary<string, object?>
            {
                ["sub"] = Subject,
                ["email"] = Email,
                ["email_verified"] = true,
                ["name"] = "SSO User",
            }));
        }

        public string Authority { get; private set; } = string.Empty;

        public async Task StartAsync()
        {
            await _app.StartAsync();
            Authority = _app.Urls.First(u => u.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            _rsa.Dispose();
        }

        private string MintIdToken(string? nonce)
        {
            var header = Base64Url("{\"alg\":\"RS256\",\"kid\":\"" + KeyId + "\",\"typ\":\"JWT\"}");
            var now = DateTimeOffset.UtcNow;
            var payload = new Dictionary<string, object?>
            {
                ["iss"] = Authority,
                ["aud"] = ClientId,
                ["sub"] = Subject,
                ["iat"] = now.ToUnixTimeSeconds(),
                ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
                ["email"] = Email,
                ["email_verified"] = true,
                ["name"] = "SSO User",
            };
            if (nonce is not null)
            {
                payload["nonce"] = nonce;
            }

            var signingInput = header + "." + Base64Url(JsonSerializer.Serialize(payload));
            var signature = _rsa.SignData(
                Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return signingInput + "." + Base64Url(signature);
        }

        private static string Base64Url(string value) => Base64Url(Encoding.ASCII.GetBytes(value));

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
