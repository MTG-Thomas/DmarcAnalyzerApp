using System.Security.Cryptography;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DmarcAnalyzer.Api.Application.Auth;

public interface IPasskeyCeremonyStore
{
    void StartRegistration(HttpResponse response, HttpRequest request, Guid userId, CredentialCreateOptions options);
    void StartAuthentication(HttpResponse response, HttpRequest request, AssertionOptions options);
    PasskeyCeremony? Consume(HttpRequest request, HttpResponse response, PasskeyCeremonyKind expectedKind);
}

public enum PasskeyCeremonyKind
{
    Registration,
    Authentication,
}

public sealed record PasskeyCeremony(
    PasskeyCeremonyKind Kind,
    Guid? UserId,
    CredentialCreateOptions? RegistrationOptions,
    AssertionOptions? AuthenticationOptions);

public sealed class PasskeyCeremonyStore(
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider,
    IServiceScopeFactory scopeFactory,
    Fido2Configuration fido2Configuration) : IPasskeyCeremonyStore
{
    private const string CookieName = "dmarc_passkey_ceremony";
    private const int MaxPendingCeremonies = 4096;
    private const int PurgeBatchSize = 1000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("dmarc-passkey-ceremony-v1");
    private readonly object _memoryLock = new();

    public void StartRegistration(HttpResponse response, HttpRequest request, Guid userId, CredentialCreateOptions options)
        => Start(response, userId, options.Challenge);

    public void StartAuthentication(HttpResponse response, HttpRequest request, AssertionOptions options)
        => Start(response, null, options.Challenge);

    public PasskeyCeremony? Consume(HttpRequest request, HttpResponse response, PasskeyCeremonyKind expectedKind)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var protectedHandle))
        {
            return null;
        }

        response.Cookies.Delete(CookieName, CookieOptions());

        string handle;
        try
        {
            handle = _protector.Unprotect(protectedHandle);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        ClaimedCeremony? claimed;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            claimed = db.Database.IsRelational()
                ? ClaimRelational(db, handle, now)
                : ClaimInMemory(db, handle, now);
        }

        if (claimed is null)
        {
            return null;
        }

        // Registration always carries a user id and authentication never
        // does, so the nullable column is the kind discriminator. The claim
        // above runs before this check: as with the old TryRemove-before-
        // verify, a wrong-kind consume still burns the ceremony.
        var kind = claimed.UserId.HasValue ? PasskeyCeremonyKind.Registration : PasskeyCeremonyKind.Authentication;
        return kind == expectedKind ? Rebuild(claimed, kind) : null;
    }

    /// <summary>
    /// Deletes expired ceremonies, oldest first, scanning at most
    /// <paramref name="batchSize"/> rows per call. Returns the rows removed.
    /// </summary>
    public int PurgeExpired(int batchSize = PurgeBatchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
        return PurgeExpired(db, timeProvider.GetUtcNow().UtcDateTime, batchSize);
    }

    private void Start(HttpResponse response, Guid? userId, byte[] challenge)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        lock (_memoryLock)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
            PurgeExpired(db, now, PurgeBatchSize);
            // Approximate under concurrency: two hosts can both pass the
            // count and both insert. It stays an abuse backstop, not a hard
            // invariant, which is all the single-host version ever was once
            // more than one replica existed.
            if (db.PasskeyCeremonyStates.Count(x => x.ConsumedAtUtc == null && x.ExpiresAtUtc > now) >= MaxPendingCeremonies)
            {
                throw new InvalidOperationException("Too many passkey ceremonies are pending.");
            }

            db.PasskeyCeremonyStates.Add(new PasskeyCeremonyState
            {
                Handle = handle,
                UserId = userId,
                Challenge = challenge,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(Lifetime),
            });
            db.SaveChanges();
        }

        response.Cookies.Append(CookieName, _protector.Protect(handle), CookieOptions());
    }

    private static ClaimedCeremony? ClaimRelational(DmarcAnalyzerDbContext db, string handle, DateTime now)
    {
        var claimed = db.Database.SqlQueryRaw<ClaimedCeremony>(
            """
            UPDATE passkey_ceremony SET "ConsumedAtUtc" = {0}, "Attempts" = "Attempts" + 1
            WHERE "Handle" = {1} AND "ConsumedAtUtc" IS NULL AND "ExpiresAtUtc" > {2}
            RETURNING "UserId", "Challenge"
            """, now, handle, now).AsEnumerable().SingleOrDefault();
        if (claimed is null)
        {
            // The claim is the one atomic statement; this second write only
            // records the attempt for abuse forensics and never revives a row.
            db.Database.ExecuteSqlRaw(
                """UPDATE passkey_ceremony SET "Attempts" = "Attempts" + 1 WHERE "Handle" = {0}""", handle);
        }

        return claimed;
    }

    private ClaimedCeremony? ClaimInMemory(DmarcAnalyzerDbContext db, string handle, DateTime now)
    {
        // The InMemory provider has no UPDATE...RETURNING, so this path is
        // for unit tests only; the lock is what makes same-instance parallel
        // consumes single-winner. Production always takes ClaimRelational.
        lock (_memoryLock)
        {
            var row = db.PasskeyCeremonyStates.SingleOrDefault(x => x.Handle == handle);
            if (row is null)
            {
                return null;
            }

            row.Attempts++;
            if (row.ConsumedAtUtc is not null || row.ExpiresAtUtc <= now)
            {
                db.SaveChanges();
                return null;
            }

            row.ConsumedAtUtc = now;
            db.SaveChanges();
            return new ClaimedCeremony { UserId = row.UserId, Challenge = row.Challenge };
        }
    }

    private static int PurgeExpired(DmarcAnalyzerDbContext db, DateTime now, int batchSize)
    {
        // Rows consume rejects (already consumed rows linger until they age
        // out, so a replay keeps reading "consumed" rather than "unknown").
        var handles = db.PasskeyCeremonyStates
            .Where(x => x.ExpiresAtUtc <= now)
            .OrderBy(x => x.ExpiresAtUtc)
            .Take(batchSize)
            .Select(x => x.Handle)
            .ToList();
        if (handles.Count == 0)
        {
            return 0;
        }

        var victims = db.PasskeyCeremonyStates.Where(x => handles.Contains(x.Handle));
        if (db.Database.IsRelational())
        {
            return victims.ExecuteDelete();
        }

        db.PasskeyCeremonyStates.RemoveRange(victims);
        db.SaveChanges();
        return handles.Count;
    }

    private PasskeyCeremony Rebuild(ClaimedCeremony claimed, PasskeyCeremonyKind kind)
    {
        // The row stores the challenge, not the options; verification reads a
        // fixed subset back (fido2-net-lib VerifyAsync: challenge, RP id,
        // user-verification, allow/exclude posture, pub-key params, and the
        // user id echoed into the result), so rebuilding from the same
        // singleton configuration the options were created with verifies
        // identically. Name and display name never leave the row because they
        // never enter verification; only the user id does.
        if (kind == PasskeyCeremonyKind.Registration)
        {
            return new PasskeyCeremony(
                kind,
                claimed.UserId,
                CredentialCreateOptions.Create(
                    fido2Configuration,
                    claimed.Challenge,
                    new Fido2User
                    {
                        Id = claimed.UserId!.Value.ToByteArray(),
                        Name = string.Empty,
                        DisplayName = string.Empty,
                    },
                    new AuthenticatorSelection
                    {
                        ResidentKey = ResidentKeyRequirement.Required,
                        UserVerification = UserVerificationRequirement.Required,
                    },
                    AttestationConveyancePreference.None,
                    [],
                    new AuthenticationExtensionsClientInputs { CredProps = true },
                    PubKeyCredParam.Defaults),
                null);
        }

        return new PasskeyCeremony(
            kind,
            null,
            null,
            AssertionOptions.Create(
                fido2Configuration,
                claimed.Challenge,
                [],
                UserVerificationRequirement.Required,
                null));
    }

    private static CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = Lifetime,
        Path = "/api/v1/",
    };

    private sealed class ClaimedCeremony
    {
        public Guid? UserId { get; set; }
        public byte[] Challenge { get; set; } = [];
    }
}
