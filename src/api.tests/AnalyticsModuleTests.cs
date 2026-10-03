using System.Net;
using System.Net.Http.Json;
using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Analytics.Spf;
using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class AnalyticsModuleTests
{
    private static readonly Guid DomainId = Guid.NewGuid();

    private sealed class StubRecordInspectionService : IRecordInspectionService
    {
        public SpfCandidateDto? Candidate { get; set; } =
            new("ready", "v=spf1 include:static.example.com -all",
                "v=spf1 ip4:198.51.100.7 -all",
                [new("include:static.example.com", "expanded", null, ["ip4:198.51.100.7"])],
                [], 1, 0, 33, 1);

        public Task<RecordInspectionDto?> InspectAsync(Guid domainId, CancellationToken ct)
            => Task.FromResult<RecordInspectionDto?>(null);

        public Task<SpfCandidateDto?> GenerateSpfCandidateAsync(Guid domainId, CancellationToken ct)
            => Task.FromResult(Candidate);
    }

    // Never called — route metadata inference needs every service type the
    // module's handlers take registered, or the first request 500s.
    private sealed class UnusedAnalyticsQueryService : IAnalyticsQueryService
    {
        public Task<AnalyticsSummaryDto> GetSummaryAsync(int days, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<DomainAnalyticsDto>> ListDomainAnalyticsAsync(int days, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<DomainDrilldownDto?> GetDomainDrilldownAsync(Guid domainId, int days, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<DomainSourceDto>?> ListDomainSourcesAsync(Guid domainId, int days, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<SourceDetailDto?> GetSourceDetailAsync(Guid domainId, string sourceIp, int days, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EnforcementGuidanceDto?> GetEnforcementGuidanceAsync(Guid domainId, int days, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<ThreatFeedDto> GetThreatFeedAsync(int days, int limit, Guid? clientId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class UnusedMtaStsInspectionService : IMtaStsInspectionService
    {
        public Task<MtaStsStateDto?> GetAsync(Guid domainId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<MtaStsStateDto?> RecheckAsync(Guid domainId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<MtaStsLiveMxDto?> GetLiveMxAsync(Guid domainId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class UnusedTlsRptQueryService : ITlsRptQueryService
    {
        public Task<TlsRptDomainSummaryDto?> GetDomainSummaryAsync(Guid domainId, int days, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<TlsRptGateSample> GetGateSampleAsync(Guid domainId, DateTime sinceUtc, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class UnusedHostnameResolver : IHostnameResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(IReadOnlyCollection<string> ips, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubSpfDriftInspectionService : ISpfDriftInspectionService
    {
        public SpfDriftStateDto? State { get; set; } = new(
            DomainId, "acme.example", Checked: true,
            SpfDriftRecordStatus.Found, "v=spf1 include:mid.example.com -all",
            [new("mid.example.com", "v=spf1 ip4:198.51.100.7 -all", "h")], [], null,
            SpfCandidateStatus.Ready, "v=spf1 ip4:198.51.100.7 -all", null, null, null,
            1, 0, 33, false, null, null, null, null, [],
            DateTime.UtcNow, null, DateTime.UtcNow, 0);

        public int Rechecks { get; private set; }

        public Task<SpfDriftStateDto?> GetAsync(Guid domainId, CancellationToken ct)
            => Task.FromResult(State);

        public Task<SpfDriftStateDto?> RecheckAsync(Guid domainId, CancellationToken ct)
        {
            Rechecks++;
            return Task.FromResult(State);
        }
    }

    [Fact]
    public async Task SpfCandidateRoute_ReturnsServiceResult()
    {
        var service = new StubRecordInspectionService();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IRecordInspectionService>(service);
        builder.Services.AddSingleton<IAnalyticsQueryService, UnusedAnalyticsQueryService>();
        builder.Services.AddSingleton<IMtaStsInspectionService, UnusedMtaStsInspectionService>();
        builder.Services.AddSingleton<ITlsRptQueryService, UnusedTlsRptQueryService>();
        builder.Services.AddSingleton<IHostnameResolver, UnusedHostnameResolver>();
        builder.Services.AddSingleton<ISpfDriftInspectionService, StubSpfDriftInspectionService>();

        await using var app = builder.Build();
        new AnalyticsModule().AddRoutes(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var url = $"/api/v1/analytics/domains/{DomainId}/spf-candidate";

        var ok = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var payload = await ok.Content.ReadFromJsonAsync<SpfCandidateDto>();
        Assert.NotNull(payload);
        Assert.Equal("ready", payload.Status);
        Assert.Equal("v=spf1 ip4:198.51.100.7 -all", payload.Candidate);

        service.Candidate = null;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task SpfDriftRoutes_ReturnServiceResult()
    {
        var service = new StubSpfDriftInspectionService();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IRecordInspectionService, StubRecordInspectionService>();
        builder.Services.AddSingleton<IAnalyticsQueryService, UnusedAnalyticsQueryService>();
        builder.Services.AddSingleton<IMtaStsInspectionService, UnusedMtaStsInspectionService>();
        builder.Services.AddSingleton<ITlsRptQueryService, UnusedTlsRptQueryService>();
        builder.Services.AddSingleton<IHostnameResolver, UnusedHostnameResolver>();
        builder.Services.AddSingleton<ISpfDriftInspectionService>(service);

        await using var app = builder.Build();
        new AnalyticsModule().AddRoutes(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var url = $"/api/v1/analytics/domains/{DomainId}/spf-drift";

        var ok = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var payload = await ok.Content.ReadFromJsonAsync<SpfDriftStateDto>();
        Assert.NotNull(payload);
        Assert.True(payload.Checked);
        Assert.Equal("v=spf1 ip4:198.51.100.7 -all", payload.CandidateText);

        var recheck = await client.PostAsync(url + "/recheck", content: null);
        Assert.Equal(HttpStatusCode.OK, recheck.StatusCode);
        Assert.Equal(1, service.Rechecks);

        service.State = null;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(url + "/recheck", content: null)).StatusCode);
    }
}
