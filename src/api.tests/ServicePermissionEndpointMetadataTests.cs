using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ServicePermissionEndpointMetadataTests
{
    [Fact]
    public void ServiceCallableEndpointsDeclareTheExpectedPermission()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        new AlertsModule().AddRoutes(app);
        new AnalyticsModule().AddRoutes(app);
        new AuditModule().AddRoutes(app);
        new ClientsModule().AddRoutes(app);
        new DomainsModule().AddRoutes(app);
        new MailboxHealthModule().AddRoutes(app);
        new MailboxSourcesModule().AddRoutes(app);
        new MailboxSyncRunsModule().AddRoutes(app);
        new MtaStsPolicyModule().AddRoutes(app);
        new NotificationRecipientsModule().AddRoutes(app);
        new ServiceApiCredentialsModule().AddRoutes(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .GroupBy(x => x.RoutePattern.RawText!)
            .ToDictionary(x => x.Key, x => x.ToArray());

        AssertPermission(endpoints, "/api/v1/analytics/summary", ServiceApiPermissions.PortfolioRead);
        AssertPermission(endpoints, "/api/v1/clients", ServiceApiPermissions.PortfolioRead, ServiceApiPermissions.ClientsManage);
        AssertPermission(endpoints, "/api/v1/domains", ServiceApiPermissions.PortfolioRead, ServiceApiPermissions.DomainsManage);
        AssertPermission(endpoints, "/api/v1/mailbox-sources", ServiceApiPermissions.PortfolioRead, ServiceApiPermissions.SourcesManage);
        AssertPermission(endpoints, "/api/v1/mailbox-sources/{id:guid}/sync", ServiceApiPermissions.SourcesSync);
        AssertPermission(endpoints, "/api/v1/alerts/{id:guid}", ServiceApiPermissions.AlertsManage);
        AssertPermission(endpoints, "/api/v1/admin/audit-events", ServiceApiPermissions.AuditRead);
        AssertPermission(endpoints, "/api/v1/notification-recipients", ServiceApiPermissions.NotificationsManage);

        AssertNoServicePermission(endpoints, "/api/v1/service-credentials");
        AssertNoServicePermission(endpoints, "/api/v1/service-credentials/permissions");
        AssertNoServicePermission(endpoints, "/api/v1/admin/notifications/test");
    }

    private static void AssertPermission(
        IReadOnlyDictionary<string, RouteEndpoint[]> endpoints,
        string path,
        params string[] expected)
    {
        var actual = endpoints[path].SelectMany(endpoint => endpoint.Metadata
            .GetOrderedMetadata<ServicePermissionMetadata>()
            .Select(x => x.Permission));
        Assert.Equal(expected, actual);
    }

    private static void AssertNoServicePermission(
        IReadOnlyDictionary<string, RouteEndpoint[]> endpoints,
        string path)
        => Assert.All(endpoints[path], endpoint =>
            Assert.Null(endpoint.Metadata.GetMetadata<ServicePermissionMetadata>()));
}
