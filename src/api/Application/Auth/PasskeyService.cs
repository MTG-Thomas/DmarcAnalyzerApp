using System.Data;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DmarcAnalyzer.Api.Application.Auth;

public sealed record PasskeyDto(
    Guid Id,
    string Name,
    DateTime CreatedAtUtc,
    DateTime? LastUsedAtUtc,
    bool IsBackupEligible,
    bool IsBackedUp);

public sealed record RegisterPasskeyRequest(string Name, AuthenticatorAttestationRawResponse Credential);
public sealed record RenamePasskeyRequest(string Name);

public interface IPasskeyService
{
    Task<ServiceResult<IReadOnlyList<PasskeyDto>>> ListAsync(CancellationToken ct);
    Task<ServiceResult<CredentialCreateOptions>> RegistrationOptionsAsync(HttpRequest request, HttpResponse response, CancellationToken ct);
    Task<ServiceResult<PasskeyDto>> RegisterAsync(RegisterPasskeyRequest request, HttpRequest httpRequest, HttpResponse response, CancellationToken ct);
    Task<ServiceResult<PasskeyDto>> RenameAsync(Guid id, RenamePasskeyRequest request, HttpRequest httpRequest, CancellationToken ct);
    Task<ServiceResult<bool>> RemoveAsync(Guid id, HttpRequest request, CancellationToken ct);
    ServiceResult<AssertionOptions> AuthenticationOptions(HttpResponse response, HttpRequest request);
    Task<ServiceResult<LoginResultDto>> AuthenticateAsync(AuthenticatorAssertionRawResponse assertion, HttpRequest request, HttpResponse response, CancellationToken ct);
}

public sealed class PasskeyService(
    DmarcAnalyzerDbContext db,
    IFido2 fido2,
    IAuthService authService,
    ICurrentUserContext currentUser,
    IPasskeyCeremonyStore ceremonies) : IPasskeyService
{
    private const int MaxPasskeysPerUser = 10;
    private static readonly TimeSpan RecentAuthenticationAge = TimeSpan.FromMinutes(15);

    public async Task<ServiceResult<IReadOnlyList<PasskeyDto>>> ListAsync(CancellationToken ct)
    {
        if (!IsHumanUser())
        {
            return ServiceResult<IReadOnlyList<PasskeyDto>>.Failure("not authenticated", 401);
        }

        var items = await db.UserPasskeys.AsNoTracking()
            .Where(x => x.UserId == currentUser.UserId)
            .OrderBy(x => x.Name)
            .Select(x => ToDto(x))
            .ToListAsync(ct);
        return ServiceResult<IReadOnlyList<PasskeyDto>>.Success(items);
    }

    public async Task<ServiceResult<CredentialCreateOptions>> RegistrationOptionsAsync(
        HttpRequest request,
        HttpResponse response,
        CancellationToken ct)
    {
        var freshness = await HasRecentAuthenticationAsync(request, ct);
        if (!freshness)
        {
            return ServiceResult<CredentialCreateOptions>.Failure("recent authentication required", 403);
        }

        var user = await db.AgencyUsers.AsNoTracking()
            .SingleAsync(x => x.Id == currentUser.UserId && x.IsActive, ct);
        var existing = await db.UserPasskeys.AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .Select(x => new { x.CredentialId, x.Transports })
            .ToListAsync(ct);

        if (existing.Count >= MaxPasskeysPerUser)
        {
            return ServiceResult<CredentialCreateOptions>.Failure("a user may have at most 10 passkeys", 409);
        }

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = user.Id.ToByteArray(),
                Name = user.Email,
                DisplayName = user.DisplayName,
            },
            ExcludeCredentials = existing.Select(x => new PublicKeyCredentialDescriptor(
                PublicKeyCredentialType.PublicKey,
                x.CredentialId,
                ParseTransports(x.Transports))).ToArray(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = new AuthenticationExtensionsClientInputs { CredProps = true },
        });

        ceremonies.StartRegistration(response, request, user.Id, options);
        return ServiceResult<CredentialCreateOptions>.Success(options);
    }

    public async Task<ServiceResult<PasskeyDto>> RegisterAsync(
        RegisterPasskeyRequest request,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken ct)
    {
        var name = NormalizeName(request.Name);
        if (name is null)
        {
            return ServiceResult<PasskeyDto>.Failure("name is required and must not exceed 100 characters", 400);
        }

        if (!await HasRecentAuthenticationAsync(httpRequest, ct))
        {
            return ServiceResult<PasskeyDto>.Failure("recent authentication required", 403);
        }

        var ceremony = ceremonies.Consume(httpRequest, response, PasskeyCeremonyKind.Registration);
        if (ceremony?.UserId != currentUser.UserId || ceremony.RegistrationOptions is null)
        {
            return ServiceResult<PasskeyDto>.Failure("passkey ceremony expired or invalid", 400);
        }

        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                : null;

            var userQuery = db.AgencyUsers.AsQueryable();
            if (db.Database.IsRelational())
            {
                userQuery = db.AgencyUsers.FromSqlInterpolated(
                    $"SELECT * FROM agency_user WHERE \"Id\" = {currentUser.UserId} FOR UPDATE");
            }
            var activeUser = await userQuery.SingleOrDefaultAsync(x => x.Id == currentUser.UserId && x.IsActive, ct);
            if (activeUser is null)
            {
                return ServiceResult<PasskeyDto>.Failure("not authenticated", 401);
            }

            var credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = request.Credential,
                OriginalOptions = ceremony.RegistrationOptions,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    !await db.UserPasskeys.AnyAsync(x => x.CredentialId == args.CredentialId, token),
            }, ct);

            if (!credential.User.Id.AsSpan().SequenceEqual(currentUser.UserId.ToByteArray()))
            {
                return ServiceResult<PasskeyDto>.Failure("passkey ceremony expired or invalid", 400);
            }

            if (await db.UserPasskeys.CountAsync(x => x.UserId == currentUser.UserId, ct) >= MaxPasskeysPerUser)
            {
                return ServiceResult<PasskeyDto>.Failure("a user may have at most 10 passkeys", 409);
            }

            var now = DateTime.UtcNow;
            var entity = new UserPasskey
            {
                UserId = currentUser.UserId,
                Name = name,
                CredentialId = credential.Id,
                PublicKey = credential.PublicKey,
                UserHandle = currentUser.UserId.ToByteArray(),
                SignCount = credential.SignCount,
                Transports = string.Join(',', credential.Transports.Select(x => x.ToString().ToLowerInvariant())),
                AaGuid = credential.AaGuid,
                IsBackupEligible = credential.IsBackupEligible,
                IsBackedUp = credential.IsBackedUp,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.UserPasskeys.Add(entity);
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return ServiceResult<PasskeyDto>.Success(ToDto(entity));
        }
        catch (Fido2VerificationException)
        {
            return ServiceResult<PasskeyDto>.Failure("passkey ceremony expired or invalid", 400);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_user_passkey_CredentialId",
        })
        {
            return ServiceResult<PasskeyDto>.Failure("passkey already registered", 409);
        }
    }

    public async Task<ServiceResult<PasskeyDto>> RenameAsync(
        Guid id,
        RenamePasskeyRequest request,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        var name = NormalizeName(request.Name);
        if (name is null)
        {
            return ServiceResult<PasskeyDto>.Failure("name is required and must not exceed 100 characters", 400);
        }
        if (!await HasRecentAuthenticationAsync(httpRequest, ct))
        {
            return ServiceResult<PasskeyDto>.Failure("recent authentication required", 403);
        }

        var passkey = await db.UserPasskeys.SingleOrDefaultAsync(
            x => x.Id == id && x.UserId == currentUser.UserId, ct);
        if (passkey is null)
        {
            return ServiceResult<PasskeyDto>.Failure("passkey not found", 404);
        }
        passkey.Name = name;
        passkey.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ServiceResult<PasskeyDto>.Success(ToDto(passkey));
    }

    public async Task<ServiceResult<bool>> RemoveAsync(Guid id, HttpRequest request, CancellationToken ct)
    {
        if (!await HasRecentAuthenticationAsync(request, ct))
        {
            return ServiceResult<bool>.Failure("recent authentication required", 403);
        }

        var passkey = await db.UserPasskeys.SingleOrDefaultAsync(
            x => x.Id == id && x.UserId == currentUser.UserId, ct);
        if (passkey is null)
        {
            return ServiceResult<bool>.Failure("passkey not found", 404);
        }
        db.UserPasskeys.Remove(passkey);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Success(true);
    }

    public ServiceResult<AssertionOptions> AuthenticationOptions(HttpResponse response, HttpRequest request)
    {
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = Array.Empty<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Required,
        });
        ceremonies.StartAuthentication(response, request, options);
        return ServiceResult<AssertionOptions>.Success(options);
    }

    public async Task<ServiceResult<LoginResultDto>> AuthenticateAsync(
        AuthenticatorAssertionRawResponse assertion,
        HttpRequest request,
        HttpResponse response,
        CancellationToken ct)
    {
        var ceremony = ceremonies.Consume(request, response, PasskeyCeremonyKind.Authentication);
        if (ceremony?.AuthenticationOptions is null)
        {
            return InvalidLogin();
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            : null;

        try
        {
            var query = db.UserPasskeys.AsQueryable();
            if (db.Database.IsRelational())
            {
                query = db.UserPasskeys.FromSqlInterpolated(
                    $"SELECT * FROM user_passkey WHERE \"CredentialId\" = {assertion.RawId} FOR UPDATE");
            }

            var passkey = db.Database.IsRelational()
                ? await query.SingleOrDefaultAsync(ct)
                : await query.SingleOrDefaultAsync(x => x.CredentialId == assertion.RawId, ct);
            if (passkey is not null)
            {
                await db.Entry(passkey).Reference(x => x.User).LoadAsync(ct);
            }
            if (passkey is null || !passkey.User.IsActive)
            {
                return InvalidLogin();
            }

            var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertion,
                OriginalOptions = ceremony.AuthenticationOptions,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = checked((uint)passkey.SignCount),
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                    args.UserHandle.AsSpan().SequenceEqual(passkey.UserHandle)
                    && args.CredentialId.AsSpan().SequenceEqual(passkey.CredentialId)),
            }, ct);

            passkey.SignCount = Math.Max(passkey.SignCount, result.SignCount);
            passkey.IsBackedUp = result.IsBackedUp;
            passkey.LastUsedAtUtc = DateTime.UtcNow;
            passkey.UpdatedAtUtc = passkey.LastUsedAtUtc.Value;
            await db.SaveChangesAsync(ct);

            var login = await authService.LoginWithExternalIdentityAsync(
                passkey.UserId,
                request.HttpContext.Connection.RemoteIpAddress?.ToString(),
                request.Headers.UserAgent.ToString(),
                ct);
            if (!login.IsSuccess)
            {
                return InvalidLogin();
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return login;
        }
        catch (Fido2VerificationException)
        {
            return InvalidLogin();
        }
        catch (OverflowException)
        {
            return InvalidLogin();
        }
    }

    private bool IsHumanUser() => currentUser.IsAuthenticated && currentUser.ActorType == "user";

    private async Task<bool> HasRecentAuthenticationAsync(HttpRequest request, CancellationToken ct)
    {
        if (!IsHumanUser() || !request.Cookies.TryGetValue(SessionCookie.Name, out var cookieId))
        {
            return false;
        }

        var threshold = DateTime.UtcNow - RecentAuthenticationAge;
        return await db.UserSessions.AnyAsync(x =>
            x.CookieId == cookieId
            && x.UserId == currentUser.UserId
            && x.RevokedAtUtc == null
            && x.CreatedAtUtc >= threshold, ct);
    }

    private static string? NormalizeName(string? name)
    {
        var value = name?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 100 ? null : value;
    }

    private static AuthenticatorTransport[]? ParseTransports(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Enum.TryParse<AuthenticatorTransport>(x, true, out var transport) ? transport : (AuthenticatorTransport?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToArray();
    }

    private static PasskeyDto ToDto(UserPasskey x) => new(
        x.Id, x.Name, x.CreatedAtUtc, x.LastUsedAtUtc, x.IsBackupEligible, x.IsBackedUp);

    private static ServiceResult<LoginResultDto> InvalidLogin()
        => ServiceResult<LoginResultDto>.Failure("invalid passkey", 401);
}
