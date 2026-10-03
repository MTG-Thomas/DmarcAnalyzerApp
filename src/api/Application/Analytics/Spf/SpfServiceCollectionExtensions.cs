using DmarcAnalyzer.Api.Application.Analytics;

namespace DmarcAnalyzer.Api.Application.Analytics.Spf;

public static class SpfServiceCollectionExtensions
{
    /// <summary>
    /// Everything SPF analysis and drift monitoring needs, registered in one
    /// place so the worker host and the API host cannot drift apart — the same
    /// bug class the backup chain hit when it was registered on only one of
    /// them. Requires AddMemoryCache, IDnsTxtResolver and IDnsMxResolver,
    /// which both hosts provide.
    /// </summary>
    public static IServiceCollection AddSpfDriftMonitoring(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSpfAnalysis();
        services.Configure<SpfDriftOptions>(configuration.GetSection("SpfDrift"));
        services.AddScoped<ISpfDriftCheckService, SpfDriftCheckService>();
        services.AddScoped<ISpfDriftStateCache, SpfDriftStateCache>();
        services.AddScoped<ISpfDriftInspectionService, SpfDriftInspectionService>();

        return services;
    }

    /// <summary>Recursive analysis (#42) plus candidate generation (#43).</summary>
    public static IServiceCollection AddSpfAnalysis(this IServiceCollection services)
    {
        services.AddScoped<ISpfDependencyAnalyzer, SpfDependencyAnalyzer>();
        services.AddScoped<ISpfCandidateGenerator, SpfCandidateGenerator>();
        services.AddSingleton<IDnsAddressResolver, DnsAddressResolver>();

        return services;
    }
}
