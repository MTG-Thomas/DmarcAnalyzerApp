using DmarcAnalyzer.Api.Application.Auth;

namespace DmarcAnalyzer.Api.Tests;

public sealed class TestCurrentUserContext : ICurrentUserContext
{
    public bool IsAuthenticated { get; init; } = true;
    public string ActorType { get; init; } = "user";
    public Guid UserId { get; init; } = Guid.NewGuid();
    public string Email { get; init; } = "test@agency.tld";
    public string Role { get; init; } = Roles.AgencyAdmin;
    public IReadOnlyCollection<Guid> AllowedClientIds { get; init; } = [];

    public bool IsAdmin => Role == Roles.AgencyAdmin;
    public bool IsAgencyStaff => Roles.IsAgencyStaff(Role);
    public bool IsService => ActorType == "service";
    public IReadOnlyCollection<string> ServicePermissions { get; init; } = [];

    public bool CanAccessClient(Guid clientId)
        => IsAgencyStaff || AllowedClientIds.Contains(clientId);

    public bool HasServicePermission(string permission)
        => IsService && ServicePermissions.Contains(permission, StringComparer.Ordinal);

    public static TestCurrentUserContext Admin() => new();

    public static TestCurrentUserContext Analyst() => new() { Role = Roles.AgencyAnalyst };

    public static TestCurrentUserContext Viewer(params Guid[] clientIds) => new()
    {
        Role = Roles.ClientViewer,
        AllowedClientIds = clientIds,
    };
}
