using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class PasskeyServiceTests
{
    [Fact]
    public async Task RegistrationOptionsUseDiscoverableUvRequiredCredential()
    {
        var user = User();
        await using var db = Db(user);
        var session = Session(user.Id, DateTime.UtcNow);
        db.AddRange(session, Passkey(user.Id, "Existing", 7));
        await db.SaveChangesAsync();
        var http = Context(session.CookieId);
        var fake = new FakeFido2();
        var ceremony = new StubCeremonyStore();
        var sut = Service(db, user.Id, fake, ceremony);

        var result = await sut.RegistrationOptionsAsync(http.Request, http.Response, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(ResidentKeyRequirement.Required, fake.RegistrationRequest!.AuthenticatorSelection.ResidentKey);
        Assert.Equal(UserVerificationRequirement.Required, fake.RegistrationRequest.AuthenticatorSelection.UserVerification);
        Assert.Equal(AttestationConveyancePreference.None, fake.RegistrationRequest.AttestationPreference);
        Assert.Single(fake.RegistrationRequest.ExcludeCredentials);
        Assert.Equal(user.Id, ceremony.Ceremony!.UserId);
    }

    [Fact]
    public async Task RegistrationOptionsRequireRecentHumanSessionAndCapTenCredentials()
    {
        var user = User();
        await using var db = Db(user);
        var session = Session(user.Id, DateTime.UtcNow);
        db.UserSessions.Add(session);
        for (var i = 0; i < 10; i++) db.UserPasskeys.Add(Passkey(user.Id, $"Key {i}", (byte)(i + 1)));
        await db.SaveChangesAsync();
        var http = Context(session.CookieId);
        var sut = Service(db, user.Id);

        var capped = await sut.RegistrationOptionsAsync(http.Request, http.Response, default);

        Assert.False(capped.IsSuccess);
        Assert.Equal(409, capped.StatusCode);

        session.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-16);
        db.UserPasskeys.RemoveRange(db.UserPasskeys);
        await db.SaveChangesAsync();
        var stale = await sut.RegistrationOptionsAsync(http.Request, http.Response, default);
        Assert.False(stale.IsSuccess);
        Assert.Equal(403, stale.StatusCode);
        Assert.Equal("recent authentication required", stale.Error);
    }

    [Fact]
    public async Task ManagementIsCurrentUserScoped()
    {
        var current = User();
        var other = User("other@example.test");
        await using var db = Db(current, other);
        var session = Session(current.Id, DateTime.UtcNow);
        var own = Passkey(current.Id, "Own", 1);
        var theirs = Passkey(other.Id, "Theirs", 2);
        db.AddRange(session, own, theirs);
        await db.SaveChangesAsync();
        var http = Context(session.CookieId);
        var sut = Service(db, current.Id);

        var list = await sut.ListAsync(default);
        var rename = await sut.RenameAsync(theirs.Id, new RenamePasskeyRequest("Stolen"), http.Request, default);
        var remove = await sut.RemoveAsync(theirs.Id, http.Request, default);

        Assert.Equal(own.Id, Assert.Single(list.Value!).Id);
        Assert.Equal(404, rename.StatusCode);
        Assert.Equal(404, remove.StatusCode);
        Assert.Equal("Theirs", (await db.UserPasskeys.SingleAsync(x => x.Id == theirs.Id)).Name);
    }

    [Fact]
    public async Task ServicePrincipalCannotManagePasskeys()
    {
        var user = User();
        await using var db = Db(user);
        var session = Session(user.Id, DateTime.UtcNow);
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        var http = Context(session.CookieId);
        var serviceContext = new TestCurrentUserContext { ActorType = "service", UserId = user.Id };
        var sut = Service(db, serviceContext);

        Assert.Equal(401, (await sut.ListAsync(default)).StatusCode);
        Assert.Equal(403, (await sut.RemoveAsync(Guid.NewGuid(), http.Request, default)).StatusCode);
    }

    [Fact]
    public async Task RegistrationPersistsOnlyVerifiedCredentialAndConsumesBoundCeremony()
    {
        var user = User();
        await using var db = Db(user);
        var session = Session(user.Id, DateTime.UtcNow);
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        var http = Context(session.CookieId);
        var fake = new FakeFido2
        {
            RegisteredCredential = new RegisteredPublicKeyCredential
            {
                Id = Enumerable.Repeat((byte)9, 32).ToArray(),
                PublicKey = new byte[64],
                User = new Fido2User { Id = user.Id.ToByteArray(), Name = user.Email, DisplayName = user.DisplayName },
                SignCount = 4,
                Transports = [AuthenticatorTransport.Internal],
                IsBackupEligible = true,
                IsBackedUp = true,
                AaGuid = Guid.NewGuid(),
                AttestationFormat = "none",
                AttestationObject = [],
                AttestationClientDataJson = [],
            },
        };
        var ceremony = new StubCeremonyStore
        {
            Ceremony = new PasskeyCeremony(PasskeyCeremonyKind.Registration, user.Id, FakeFido2.CreateOptions(user.Id), null),
        };
        var sut = Service(db, user.Id, fake, ceremony);

        var result = await sut.RegisterAsync(
            new RegisterPasskeyRequest("  Laptop  ", new AuthenticatorAttestationRawResponse()),
            http.Request,
            http.Response,
            default);

        Assert.True(result.IsSuccess);
        var stored = await db.UserPasskeys.SingleAsync();
        Assert.Equal("Laptop", stored.Name);
        Assert.Equal(4, stored.SignCount);
        Assert.True(stored.IsBackupEligible);
        Assert.True(stored.IsBackedUp);
        Assert.Equal("internal", stored.Transports);
        Assert.Equal(PasskeyCeremonyKind.Registration, ceremony.ConsumedKind);
    }

    [Fact]
    public async Task RegistrationRejectsWrongUserAndVerificationFailure()
    {
        var user = User();
        await using var db = Db(user);
        var session = Session(user.Id, DateTime.UtcNow);
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        var http = Context(session.CookieId);
        var ceremony = new StubCeremonyStore
        {
            Ceremony = new PasskeyCeremony(PasskeyCeremonyKind.Registration, user.Id, FakeFido2.CreateOptions(user.Id), null),
        };
        var fake = new FakeFido2
        {
            RegisteredCredential = new RegisteredPublicKeyCredential
            {
                Id = new byte[32], PublicKey = new byte[64],
                User = new Fido2User { Id = Guid.NewGuid().ToByteArray(), Name = "wrong", DisplayName = "Wrong" },
                Transports = [], AttestationFormat = "none", AttestationObject = [], AttestationClientDataJson = [],
            },
        };
        var sut = Service(db, user.Id, fake, ceremony);

        Assert.Equal(400, (await sut.RegisterAsync(
            new RegisterPasskeyRequest("Key", new AuthenticatorAttestationRawResponse()),
            http.Request, http.Response, default)).StatusCode);
        Assert.Empty(db.UserPasskeys);

        ceremony.Ceremony = new PasskeyCeremony(PasskeyCeremonyKind.Registration, user.Id, FakeFido2.CreateOptions(user.Id), null);
        fake.VerificationFailure = true;
        Assert.Equal(400, (await sut.RegisterAsync(
            new RegisterPasskeyRequest("Key", new AuthenticatorAttestationRawResponse()),
            http.Request, http.Response, default)).StatusCode);
    }

    [Fact]
    public async Task AuthenticationUpdatesCredentialAndMintsExistingSession()
    {
        var user = User();
        await using var db = Db(user);
        var passkey = Passkey(user.Id, "Key", 3);
        passkey.SignCount = 4;
        db.UserPasskeys.Add(passkey);
        await db.SaveChangesAsync();
        var http = Context("none");
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        http.Request.Headers.UserAgent = "test-browser";
        var fake = new FakeFido2
        {
            AssertionResult = new VerifyAssertionResult
            {
                CredentialId = passkey.CredentialId,
                SignCount = 5,
                IsBackedUp = true,
            },
        };
        var ceremony = new StubCeremonyStore
        {
            Ceremony = new PasskeyCeremony(PasskeyCeremonyKind.Authentication, null, null, FakeFido2.AssertionOptions()),
        };
        var sut = Service(db, user.Id, fake, ceremony);
        var assertion = new AuthenticatorAssertionRawResponse { RawId = passkey.CredentialId };

        var result = await sut.AuthenticateAsync(assertion, http.Request, http.Response, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, result.Value!.User.Id);
        Assert.Equal(5, passkey.SignCount);
        Assert.True(passkey.IsBackedUp);
        Assert.NotNull(passkey.LastUsedAtUtc);
        var session = await db.UserSessions.SingleAsync();
        Assert.Equal("test-browser", session.UserAgent);
        Assert.Equal(System.Net.IPAddress.Loopback.ToString(), session.IpAddress);
    }

    [Fact]
    public async Task AuthenticationFailsUniformlyForMissingCeremonyCredentialOrInactiveUser()
    {
        var user = User();
        await using var db = Db(user);
        var passkey = Passkey(user.Id, "Key", 3);
        db.UserPasskeys.Add(passkey);
        await db.SaveChangesAsync();
        var http = Context("none");
        var fake = new FakeFido2();
        var ceremony = new StubCeremonyStore();
        var sut = Service(db, user.Id, fake, ceremony);

        var missingCeremony = await sut.AuthenticateAsync(
            new AuthenticatorAssertionRawResponse { RawId = passkey.CredentialId },
            http.Request, http.Response, default);
        Assert.Equal((401, "invalid passkey"), (missingCeremony.StatusCode, missingCeremony.Error));

        ceremony.Ceremony = new PasskeyCeremony(PasskeyCeremonyKind.Authentication, null, null, FakeFido2.AssertionOptions());
        var missingCredential = await sut.AuthenticateAsync(
            new AuthenticatorAssertionRawResponse { RawId = new byte[32] },
            http.Request, http.Response, default);
        Assert.Equal((401, "invalid passkey"), (missingCredential.StatusCode, missingCredential.Error));

        user.IsActive = false;
        await db.SaveChangesAsync();
        ceremony.Ceremony = new PasskeyCeremony(PasskeyCeremonyKind.Authentication, null, null, FakeFido2.AssertionOptions());
        var inactive = await sut.AuthenticateAsync(
            new AuthenticatorAssertionRawResponse { RawId = passkey.CredentialId },
            http.Request, http.Response, default);
        Assert.Equal((401, "invalid passkey"), (inactive.StatusCode, inactive.Error));
    }

    private static PasskeyService Service(DmarcAnalyzerDbContext db, Guid userId)
        => Service(db, new TestCurrentUserContext { UserId = userId });

    private static PasskeyService Service(
        DmarcAnalyzerDbContext db,
        Guid userId,
        IFido2 fido,
        IPasskeyCeremonyStore ceremony)
        => new(db, fido, new AuthService(db), new TestCurrentUserContext { UserId = userId }, ceremony);

    private static PasskeyService Service(DmarcAnalyzerDbContext db, ICurrentUserContext context)
    {
        var fido = new Fido2(new Fido2Configuration
        {
            ServerDomain = "dmarc.midtowntg.com",
            ServerName = "DMARC Analyzer",
            Origins = new HashSet<string> { "https://dmarc.midtowntg.com" },
            ChallengeSize = 32,
        });
        return new PasskeyService(db, fido, new AuthService(db), context, new StubCeremonyStore());
    }

    private static DmarcAnalyzerDbContext Db(params AgencyUser[] users)
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DmarcAnalyzerDbContext(options);
        db.AgencyUsers.AddRange(users);
        return db;
    }

    private static AgencyUser User(string email = "admin@example.test") => new()
    {
        Email = email,
        DisplayName = email,
        PasswordHash = "not-used",
        Role = Roles.AgencyAdmin,
        IsActive = true,
    };

    private static UserSession Session(Guid userId, DateTime created) => new()
    {
        UserId = userId,
        CookieId = Guid.NewGuid().ToString("N"),
        CreatedAtUtc = created,
        LastSeenAtUtc = created,
        ExpiresAtUtc = created.AddDays(7),
    };

    private static UserPasskey Passkey(Guid userId, string name, byte discriminator) => new()
    {
        UserId = userId,
        Name = name,
        CredentialId = Enumerable.Repeat(discriminator, 32).ToArray(),
        PublicKey = new byte[64],
        UserHandle = userId.ToByteArray(),
    };

    private static DefaultHttpContext Context(string cookieId)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SessionCookie.Name}={cookieId}";
        return context;
    }

    private sealed class StubCeremonyStore : IPasskeyCeremonyStore
    {
        public PasskeyCeremony? Ceremony { get; set; }
        public PasskeyCeremonyKind? ConsumedKind { get; private set; }
        public void StartRegistration(HttpResponse response, HttpRequest request, Guid userId, CredentialCreateOptions options)
            => Ceremony = new PasskeyCeremony(PasskeyCeremonyKind.Registration, userId, options, null);
        public void StartAuthentication(HttpResponse response, HttpRequest request, AssertionOptions options)
            => Ceremony = new PasskeyCeremony(PasskeyCeremonyKind.Authentication, null, null, options);
        public PasskeyCeremony? Consume(HttpRequest request, HttpResponse response, PasskeyCeremonyKind expectedKind)
        {
            ConsumedKind = expectedKind;
            var result = Ceremony;
            Ceremony = null;
            return result;
        }
    }

    private sealed class FakeFido2 : IFido2
    {
        public RequestNewCredentialParams? RegistrationRequest { get; private set; }
        public RegisteredPublicKeyCredential? RegisteredCredential { get; set; }
        public VerifyAssertionResult? AssertionResult { get; set; }
        public bool VerificationFailure { get; set; }

        public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams getAssertionOptionsParams)
            => AssertionOptions();

        public Task<VerifyAssertionResult> MakeAssertionAsync(MakeAssertionParams makeAssertionParams, CancellationToken cancellationToken = default)
            => VerificationFailure
                ? Task.FromException<VerifyAssertionResult>(new Fido2VerificationException("invalid"))
                : Task.FromResult(AssertionResult ?? new VerifyAssertionResult
                {
                    CredentialId = makeAssertionParams.AssertionResponse.RawId,
                    SignCount = makeAssertionParams.StoredSignatureCounter,
                });

        public Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(MakeNewCredentialParams makeNewCredentialParams, CancellationToken cancellationToken = default)
            => VerificationFailure
                ? Task.FromException<RegisteredPublicKeyCredential>(new Fido2VerificationException("invalid"))
                : Task.FromResult(RegisteredCredential!);

        public CredentialCreateOptions RequestNewCredential(RequestNewCredentialParams requestNewCredentialParams)
        {
            RegistrationRequest = requestNewCredentialParams;
            return CreateOptions(new Guid(requestNewCredentialParams.User.Id));
        }

        public static CredentialCreateOptions CreateOptions(Guid userId) => new()
        {
            Rp = new PublicKeyCredentialRpEntity("dmarc.midtowntg.com", "DMARC Analyzer", null),
            User = new Fido2User { Id = userId.ToByteArray(), Name = "user", DisplayName = "User" },
            Challenge = new byte[32],
            PubKeyCredParams = PubKeyCredParam.Defaults,
            AuthenticatorSelection = new AuthenticatorSelection(),
        };

        public static AssertionOptions AssertionOptions() => new()
        {
            Challenge = new byte[32],
            RpId = "dmarc.midtowntg.com",
            UserVerification = UserVerificationRequirement.Required,
        };
    }
}
