using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class PasskeyHostTests : IClassFixture<PasskeyHostFactory>
{
    private const string Origin = "https://dmarc.midtowntg.com";
    private readonly PasskeyHostFactory _factory;

    public PasskeyHostTests(PasskeyHostFactory factory) => _factory = factory;

    [Fact]
    public async Task ProvidersExposePasskeysAndAnonymousOptionsAreExactOriginOnly()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var providers = await client.GetFromJsonAsync<Providers>("/api/v1/auth/providers");
        Assert.True(providers!.Passkeys);

        using var refused = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/passkeys/options");
        refused.Headers.Add("Origin", "https://other.midtowntg.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(refused)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/passkeys/options");
        request.Headers.Add("Origin", Origin);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<AssertionOptions>();
        Assert.Equal("dmarc.midtowntg.com", options!.RpId);
        Assert.Equal(UserVerificationRequirement.Required, options.UserVerification);
        Assert.Empty(options.AllowCredentials);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), x => x.Contains("dmarc_passkey_ceremony=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthenticatedLifecycleIsCurrentUserAndRecentSessionBound()
    {
        var (user, session) = await _factory.SeedUserAsync(DateTime.UtcNow);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie.Name}={session.CookieId}");

        var list = await client.GetFromJsonAsync<PasskeysResponse>("/api/v1/passkeys");
        Assert.Empty(list!.Passkeys);

        using var optionsRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/passkeys/options");
        optionsRequest.Headers.Add("Cookie", $"{SessionCookie.Name}={session.CookieId}");
        optionsRequest.Headers.Add("Origin", Origin);
        var optionsResponse = await client.SendAsync(optionsRequest);
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        var options = await optionsResponse.Content.ReadFromJsonAsync<CredentialCreateOptions>();
        Assert.Equal(user.Id.ToByteArray(), options!.User.Id);
        Assert.Equal(ResidentKeyRequirement.Required, options.AuthenticatorSelection.ResidentKey);

        var stale = await _factory.SeedUserAsync(DateTime.UtcNow.AddMinutes(-16), "stale@example.test");
        using var staleRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/passkeys/options");
        staleRequest.Headers.Add("Cookie", $"{SessionCookie.Name}={stale.Session.CookieId}");
        staleRequest.Headers.Add("Origin", Origin);
        var staleResponse = await client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);
        Assert.Equal("recent authentication required", (await staleResponse.Content.ReadFromJsonAsync<Error>())!.ErrorMessage);
    }

    [Fact]
    public async Task FullLifecycleRegistersAuthenticatesRenamesAndRemovesForCurrentUser()
    {
        var (user, session) = await _factory.SeedUserAsync(DateTime.UtcNow, "lifecycle@example.test");
        using var managementClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(Origin),
            HandleCookies = true,
        });
        managementClient.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie.Name}={session.CookieId}");

        using var optionsRequest = OriginRequest(HttpMethod.Post, "/api/v1/passkeys/options");
        Assert.Equal(HttpStatusCode.OK, (await managementClient.SendAsync(optionsRequest)).StatusCode);

        var credentialId = Enumerable.Repeat((byte)42, 32).ToArray();
        using var registerRequest = OriginRequest(HttpMethod.Post, "/api/v1/passkeys");
        registerRequest.Content = JsonContent.Create(new RegisterPasskeyRequest(
            "Laptop",
            Attestation(credentialId)));
        var registeredResponse = await managementClient.SendAsync(registerRequest);
        Assert.Equal(HttpStatusCode.Created, registeredResponse.StatusCode);
        var registered = await registeredResponse.Content.ReadFromJsonAsync<PasskeyDto>();
        Assert.Equal("Laptop", registered!.Name);
        Assert.True(registered.IsBackupEligible);
        Assert.True(registered.IsBackedUp);

        using var renameRequest = OriginRequest(HttpMethod.Put, $"/api/v1/passkeys/{registered.Id}");
        renameRequest.Content = JsonContent.Create(new RenamePasskeyRequest("Security key"));
        var renamedResponse = await managementClient.SendAsync(renameRequest);
        Assert.Equal(HttpStatusCode.OK, renamedResponse.StatusCode);
        Assert.Equal("Security key", (await renamedResponse.Content.ReadFromJsonAsync<PasskeyDto>())!.Name);

        using var authenticationClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(Origin),
            HandleCookies = true,
        });
        using var authenticationOptions = OriginRequest(HttpMethod.Post, "/api/v1/auth/passkeys/options");
        Assert.Equal(HttpStatusCode.OK, (await authenticationClient.SendAsync(authenticationOptions)).StatusCode);
        using var completeRequest = OriginRequest(HttpMethod.Post, "/api/v1/auth/passkeys/complete");
        completeRequest.Content = JsonContent.Create(Assertion(credentialId, user.Id));
        var completeResponse = await authenticationClient.SendAsync(completeRequest);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        Assert.Contains(completeResponse.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith($"{SessionCookie.Name}=", StringComparison.Ordinal));
        Assert.Equal(user.Id, JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("user").GetProperty("id").GetGuid());

        // The ceremony was consumed before verification; replay is a generic failure.
        using var replayRequest = OriginRequest(HttpMethod.Post, "/api/v1/auth/passkeys/complete");
        replayRequest.Content = JsonContent.Create(Assertion(credentialId, user.Id));
        var replayResponse = await authenticationClient.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        Assert.Equal("invalid passkey", (await replayResponse.Content.ReadFromJsonAsync<Error>())!.ErrorMessage);

        using var deleteRequest = OriginRequest(HttpMethod.Delete, $"/api/v1/passkeys/{registered.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await managementClient.SendAsync(deleteRequest)).StatusCode);
        Assert.Empty((await managementClient.GetFromJsonAsync<PasskeysResponse>("/api/v1/passkeys"))!.Passkeys);
    }

    private static HttpRequestMessage OriginRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", Origin);
        return request;
    }

    private static AuthenticatorAttestationRawResponse Attestation(byte[] credentialId) => new()
    {
        Id = WebEncoders.Base64UrlEncode(credentialId),
        RawId = credentialId,
        Type = PublicKeyCredentialType.PublicKey,
        Response = new AuthenticatorAttestationRawResponse.AttestationResponse
        {
            AttestationObject = [],
            ClientDataJson = [],
            Transports = [AuthenticatorTransport.Internal],
        },
        ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
    };

    private static AuthenticatorAssertionRawResponse Assertion(byte[] credentialId, Guid userId) => new()
    {
        Id = WebEncoders.Base64UrlEncode(credentialId),
        RawId = credentialId,
        Type = PublicKeyCredentialType.PublicKey,
        Response = new AuthenticatorAssertionRawResponse.AssertionResponse
        {
            AuthenticatorData = [],
            Signature = [],
            ClientDataJson = [],
            UserHandle = userId.ToByteArray(),
        },
        ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
    };

    private sealed record Providers(bool Passkeys);
    private sealed record PasskeysResponse(IReadOnlyList<PasskeyDto> Passkeys);
    private sealed record Error([property: System.Text.Json.Serialization.JsonPropertyName("error")] string ErrorMessage);
}

public sealed class PasskeyHostFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"passkey-host-{Guid.NewGuid():N}";
    private readonly HostFakeFido2 _fido2 = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auth:Passkeys:Enabled"] = "true",
                ["Auth:Passkeys:RelyingPartyId"] = "dmarc.midtowntg.com",
                ["Auth:Passkeys:RelyingPartyName"] = "DMARC Analyzer",
                ["Auth:Passkeys:Origins:0"] = "https://dmarc.midtowntg.com",
                ["Database:MigrateOnStartup"] = "false",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<DmarcAnalyzerDbContext>>();
            services.RemoveAll<DmarcAnalyzerDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<DmarcAnalyzerDbContext>>();
            services.AddDbContext<DmarcAnalyzerDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
            services.RemoveAll<IFido2>();
            services.AddSingleton<IFido2>(_fido2);
        });
    }

    public async Task<(AgencyUser User, UserSession Session)> SeedUserAsync(DateTime sessionCreated, string email = "host@example.test")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        var user = new AgencyUser
        {
            Email = email,
            DisplayName = "Host test",
            PasswordHash = "not-used",
            Role = Roles.AgencyAdmin,
        };
        var session = new UserSession
        {
            UserId = user.Id,
            CookieId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = sessionCreated,
            LastSeenAtUtc = sessionCreated,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
        };
        db.AddRange(user, session);
        await db.SaveChangesAsync();
        return (user, session);
    }

    private sealed class HostFakeFido2 : IFido2
    {
        public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams parameters) => new()
        {
            Challenge = Enumerable.Repeat((byte)1, 32).ToArray(),
            RpId = "dmarc.midtowntg.com",
            UserVerification = parameters.UserVerification,
            AllowCredentials = parameters.AllowedCredentials,
        };

        public CredentialCreateOptions RequestNewCredential(RequestNewCredentialParams parameters) => new()
        {
            Rp = new PublicKeyCredentialRpEntity("dmarc.midtowntg.com", "DMARC Analyzer", null),
            User = parameters.User,
            Challenge = Enumerable.Repeat((byte)2, 32).ToArray(),
            PubKeyCredParams = PubKeyCredParam.Defaults,
            AuthenticatorSelection = parameters.AuthenticatorSelection,
            Attestation = parameters.AttestationPreference,
            ExcludeCredentials = parameters.ExcludeCredentials,
        };

        public async Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(
            MakeNewCredentialParams parameters,
            CancellationToken cancellationToken = default)
        {
            var credentialId = parameters.AttestationResponse.RawId;
            if (!await parameters.IsCredentialIdUniqueToUserCallback(
                new IsCredentialIdUniqueToUserParams(credentialId, parameters.OriginalOptions.User), cancellationToken))
            {
                throw new Fido2VerificationException("duplicate");
            }
            return new RegisteredPublicKeyCredential
            {
                Id = credentialId,
                PublicKey = Enumerable.Repeat((byte)3, 64).ToArray(),
                User = parameters.OriginalOptions.User,
                SignCount = 0,
                Transports = parameters.AttestationResponse.Response.Transports,
                IsBackupEligible = true,
                IsBackedUp = true,
                AaGuid = Guid.NewGuid(),
                AttestationFormat = "none",
                AttestationObject = [],
                AttestationClientDataJson = [],
            };
        }

        public async Task<VerifyAssertionResult> MakeAssertionAsync(
            MakeAssertionParams parameters,
            CancellationToken cancellationToken = default)
        {
            var userHandle = parameters.AssertionResponse.Response.UserHandle ?? [];
            if (!await parameters.IsUserHandleOwnerOfCredentialIdCallback(
                new IsUserHandleOwnerOfCredentialIdParams(parameters.AssertionResponse.RawId, userHandle), cancellationToken))
            {
                throw new Fido2VerificationException("wrong owner");
            }
            return new VerifyAssertionResult
            {
                CredentialId = parameters.AssertionResponse.RawId,
                SignCount = parameters.StoredSignatureCounter + 1,
                IsBackedUp = true,
            };
        }
    }
}
