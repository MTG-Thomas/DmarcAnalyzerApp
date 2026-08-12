using System.Collections.Concurrent;
using System.Security.Cryptography;
using Fido2NetLib;
using Microsoft.AspNetCore.DataProtection;

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
    TimeProvider timeProvider) : IPasskeyCeremonyStore
{
    private const string CookieName = "dmarc_passkey_ceremony";
    private const int MaxPendingCeremonies = 4096;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("dmarc-passkey-ceremony-v1");
    private readonly ConcurrentDictionary<string, PendingCeremony> _pending = new();
    private readonly object _startLock = new();

    public void StartRegistration(HttpResponse response, HttpRequest request, Guid userId, CredentialCreateOptions options)
        => Start(response, request, new PasskeyCeremony(PasskeyCeremonyKind.Registration, userId, options, null));

    public void StartAuthentication(HttpResponse response, HttpRequest request, AssertionOptions options)
        => Start(response, request, new PasskeyCeremony(PasskeyCeremonyKind.Authentication, null, null, options));

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

        if (!_pending.TryRemove(handle, out var pending) || pending.ExpiresAtUtc < timeProvider.GetUtcNow())
        {
            return null;
        }

        // TryRemove happens before protocol verification: concurrent or later replays fail.
        return pending.Ceremony.Kind == expectedKind ? pending.Ceremony : null;
    }

    private void Start(HttpResponse response, HttpRequest request, PasskeyCeremony ceremony)
    {
        string handle;
        lock (_startLock)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var stale in _pending.Where(x => x.Value.ExpiresAtUtc < now))
            {
                _pending.TryRemove(stale.Key, out _);
            }
            if (_pending.Count >= MaxPendingCeremonies)
            {
                throw new InvalidOperationException("Too many passkey ceremonies are pending.");
            }

            handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _pending[handle] = new PendingCeremony(ceremony, now.Add(Lifetime));
        }

        response.Cookies.Append(CookieName, _protector.Protect(handle), CookieOptions());
    }

    private static CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = Lifetime,
        Path = "/api/v1/",
    };

    private sealed record PendingCeremony(PasskeyCeremony Ceremony, DateTimeOffset ExpiresAtUtc);
}
