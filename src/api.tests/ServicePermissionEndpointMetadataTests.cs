using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.ReportSources;
using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ServicePermissionEndpointMetadataTests
{
    [Fact]
    public void ServiceCallableEndpointsDeclareTheExpectedPermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        builder.Services.AddScoped<ICurrentUserContext>(_ => TestCurrentUserContext.Admin());
        builder.Services.AddScoped<IServiceApiCredentialService, ServiceApiCredentialService>();
        builder.Services.AddScoped<IAnalyticsQueryService>(_ => null!);
        builder.Services.AddScoped<IHostnameResolver>(_ => null!);
        builder.Services.AddScoped<IAlertEvaluationService>(_ => null!);
        builder.Services.AddScoped<IDigestService>(_ => null!);
        builder.Services.AddScoped<IEmailSender>(_ => null!);
        builder.Services.AddScoped<AuditQueryService>(_ => null!);
        builder.Services.AddScoped<IClientService>(_ => null!);
        builder.Services.AddScoped<IDomainService>(_ => null!);
        builder.Services.AddScoped<IMailboxHealthQueryService>(_ => null!);
        builder.Services.AddScoped<IReportSourceService>(_ => null!);
        builder.Services.AddScoped<IMailboxSyncService>(_ => null!);
        builder.Services.AddScoped<IMailboxSyncRunQueryService>(_ => null!);
        builder.Services.AddScoped<IMtaStsPolicyAdminService>(_ => null!);
        builder.Services.AddScoped<IMtaStsInspectionService>(_ => null!);
        builder.Services.AddScoped<IRecordInspectionService>(_ => null!);
        builder.Services.AddScoped<ITlsRptQueryService>(_ => null!);
        builder.Services.AddScoped<IAuditLog>(_ => null!);
        var app = builder.Build();
        new AlertsModule().AddRoutes(app);
        new AnalyticsModule().AddRoutes(app);
        new AuditModule().AddRoutes(app);
        new ClientsModule().AddRoutes(app);
        new DomainsModule().AddRoutes(app);
        new MailboxHealthModule().AddRoutes(app);
        new ReportSourcesModule().AddRoutes(app);
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
        AssertPermission(endpoints, "/api/v1/report-sources", ServiceApiPermissions.PortfolioRead, ServiceApiPermissions.SourcesManage);
        AssertPermission(endpoints, "/api/v1/report-sources/{id:guid}/sync", ServiceApiPermissions.SourcesSync);
        AssertPermission(endpoints, "/api/v1/alerts/{id:guid}", ServiceApiPermissions.AlertsManage);
        AssertPermission(endpoints, "/api/v1/admin/audit-events", ServiceApiPermissions.AuditRead);
        AssertPermission(endpoints, "/api/v1/notification-recipients",
            ServiceApiPermissions.NotificationsManage, ServiceApiPermissions.NotificationsManage);

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
