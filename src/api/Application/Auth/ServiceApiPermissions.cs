namespace DmarcAnalyzer.Api.Application.Auth;

public sealed record ServiceApiPermissionDto(string Id, string Name, string Description);

public static class ServiceApiPermissions
{
    public const string PortfolioRead = "portfolio.read";
    public const string AlertsManage = "alerts.manage";
    public const string ClientsManage = "clients.manage";
    public const string DomainsManage = "domains.manage";
    public const string SourcesManage = "sources.manage";
    public const string SourcesSync = "sources.sync";
    public const string NotificationsManage = "notifications.manage";
    public const string AuditRead = "audit.read";

    public static readonly IReadOnlyList<ServiceApiPermissionDto> Catalog =
    [
        new(PortfolioRead, "Portfolio read access", "Read clients, domains, analytics, alerts, and report-source status."),
        new(AlertsManage, "Alert operations", "Acknowledge or close alerts and run alert evaluation."),
        new(ClientsManage, "Client management", "Create and update clients."),
        new(DomainsManage, "Domain and MTA-STS management", "Create and update domains, hosted policies, and live checks."),
        new(SourcesManage, "API report source management", "Create and update API report sources. Mailbox and API credential lifecycle remains unavailable."),
        new(SourcesSync, "Report source sync", "Trigger a bounded manual report-source sync."),
        new(NotificationsManage, "Notification management", "Manage recipients and preview or send scheduled digests."),
        new(AuditRead, "Audit read access", "Query the immutable audit trail."),
    ];

    private static readonly IReadOnlyDictionary<string, int> Order = Catalog
        .Select((permission, index) => (permission.Id, index))
        .ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);

    public static bool TryNormalize(
        IReadOnlyCollection<string>? requested,
        out string[] permissions,
        out string? error)
    {
        permissions = [];
        error = null;
        if (requested is null || requested.Count == 0)
        {
            error = "at least one permission is required";
            return false;
        }

        var normalized = requested
            .Select(x => x?.Trim() ?? string.Empty)
            .ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            error = "permissions must not contain duplicates";
            return false;
        }

        var unknown = normalized.FirstOrDefault(x => !Order.ContainsKey(x));
        if (unknown is not null)
        {
            error = $"unknown permission: {unknown}";
            return false;
        }

        permissions = normalized.OrderBy(x => Order[x]).ToArray();
        return true;
    }
}
