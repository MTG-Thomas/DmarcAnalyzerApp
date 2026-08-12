namespace DmarcAnalyzer.Api.Application.Auth;

public sealed class CurrentUserContext : ICurrentUserContext
{
    private HashSet<Guid> _allowedClientIds = [];

    public bool IsAuthenticated { get; private set; }
    public string ActorType { get; private set; } = "anonymous";
    public Guid UserId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;
    public bool IsAdmin => Role == Roles.AgencyAdmin;
    public bool IsAgencyStaff => Roles.IsAgencyStaff(Role);
    public bool IsService => ActorType == "service";
    public IReadOnlyCollection<string> ServicePermissions { get; private set; } = [];
    public IReadOnlyCollection<Guid> AllowedClientIds => _allowedClientIds;

    public bool CanAccessClient(Guid clientId)
        => IsAgencyStaff || _allowedClientIds.Contains(clientId);

    public bool HasServicePermission(string permission)
        => IsService && ServicePermissions.Contains(permission, StringComparer.Ordinal);

    internal void Set(UserDto user, IReadOnlyList<Guid> grantedClientIds)
    {
        IsAuthenticated = true;
        ActorType = "user";
        UserId = user.Id;
        Email = user.Email;
        Role = user.Role;
        _allowedClientIds = [.. grantedClientIds];
        ServicePermissions = [];
    }

    internal void SetService(ServiceApiPrincipal principal)
    {
        IsAuthenticated = true;
        ActorType = "service";
        UserId = principal.CredentialId;
        Email = $"service:{principal.Name}";
        Role = Roles.AgencyAnalyst;
        _allowedClientIds = [];
        ServicePermissions = principal.Permissions;
    }
}
