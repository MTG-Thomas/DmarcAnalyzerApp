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
    public IReadOnlyCollection<Guid> AllowedClientIds => _allowedClientIds;

    public bool CanAccessClient(Guid clientId)
        => IsAgencyStaff || _allowedClientIds.Contains(clientId);

    internal void Set(UserDto user, IReadOnlyList<Guid> grantedClientIds)
    {
        IsAuthenticated = true;
        ActorType = "user";
        UserId = user.Id;
        Email = user.Email;
        Role = user.Role;
        _allowedClientIds = [.. grantedClientIds];
    }

    internal void SetService(ServiceApiPrincipal principal)
    {
        IsAuthenticated = true;
        ActorType = "service";
        UserId = principal.CredentialId;
        Email = $"service:{principal.Name}";
        Role = Roles.AgencyAnalyst;
        _allowedClientIds = [];
    }
}
