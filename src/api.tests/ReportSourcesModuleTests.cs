using System.Net;
using System.Net.Http.Json;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.ReportSources;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using DmarcAnalyzer.Api.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ReportSourcesModuleTests
{
    private static readonly Guid SourceId = Guid.NewGuid();

    private static readonly ReportSourceDto Source = new(
        SourceId, "Inbox", "imap", "imap.example", 993, true, "reports@example",
        Guid.NewGuid(), "Acme", true, false, null, null, null, null,
        DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task RoutesReturnServiceStatuses()
    {
        var sources = new StubReportSourceService();
        var sync = new StubMailboxSyncService();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IReportSourceService>(sources);
        builder.Services.AddSingleton<IMailboxSyncService>(sync);
        builder.Services.AddSingleton<IAuditLog>(new StubAuditLog());

        await using var app = builder.Build();
        new ReportSourcesModule().AddRoutes(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/report-sources")).StatusCode);

        sources.CreateResult = ServiceResult<ReportSourceDto>.Failure("invalid", 400);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            "/api/v1/report-sources", new CreateReportSourceRequest())).StatusCode);
        sources.CreateResult = ServiceResult<ReportSourceDto>.Success(Source);
        var created = await client.PostAsJsonAsync("/api/v1/report-sources", new CreateReportSourceRequest());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/report-sources/{SourceId}", created.Headers.Location?.OriginalString);

        sources.UpdateResult = ServiceResult<ReportSourceDto>.Failure("not found", 404);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync(
            $"/api/v1/report-sources/{SourceId}", new UpdateReportSourceRequest())).StatusCode);
        sources.UpdateResult = ServiceResult<ReportSourceDto>.Failure("invalid", 409);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(
            $"/api/v1/report-sources/{SourceId}", new UpdateReportSourceRequest())).StatusCode);
        sources.UpdateResult = ServiceResult<ReportSourceDto>.Success(Source);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(
            $"/api/v1/report-sources/{SourceId}", new UpdateReportSourceRequest { DeleteAfterRetention = true })).StatusCode);

        sync.Result = ServiceResult<MailboxSyncResult>.Failure("not found", 404);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(
            $"/api/v1/report-sources/{SourceId}/sync", null)).StatusCode);
        sync.Result = ServiceResult<MailboxSyncResult>.Failure("busy", 409);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(
            $"/api/v1/report-sources/{SourceId}/sync", null)).StatusCode);
        sync.Result = ServiceResult<MailboxSyncResult>.Success(SyncResult(false));
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync(
            $"/api/v1/report-sources/{SourceId}/sync", null)).StatusCode);
        sync.Result = ServiceResult<MailboxSyncResult>.Success(SyncResult(true));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(
            $"/api/v1/report-sources/{SourceId}/sync", null)).StatusCode);
    }

    private static MailboxSyncResult SyncResult(bool success) => new(
        SourceId, 1, 1, 1, 0, 0, 0, 0, success, success ? null : "failed",
        DateTime.UtcNow, DateTime.UtcNow);

    private sealed class StubReportSourceService : IReportSourceService
    {
        public ServiceResult<ReportSourceDto> CreateResult { get; set; } = ServiceResult<ReportSourceDto>.Success(Source);
        public ServiceResult<ReportSourceDto> UpdateResult { get; set; } = ServiceResult<ReportSourceDto>.Success(Source);

        public Task<IReadOnlyList<ReportSourceDto>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ReportSourceDto>>([Source]);

        public Task<ServiceResult<ReportSourceDto>> CreateAsync(CreateReportSourceRequest request, CancellationToken ct)
            => Task.FromResult(CreateResult);

        public Task<ServiceResult<ReportSourceDto>> UpdateAsync(Guid id, UpdateReportSourceRequest request, CancellationToken ct)
            => Task.FromResult(UpdateResult);
    }

    private sealed class StubMailboxSyncService : IMailboxSyncService
    {
        public ServiceResult<MailboxSyncResult> Result { get; set; } = ServiceResult<MailboxSyncResult>.Success(SyncResult(true));

        public Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, CancellationToken ct)
            => Task.FromResult(Result);

        public Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(
            Guid reportSourceId, string trigger, CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class StubAuditLog : IAuditLog
    {
        public Task RecordAsync(
            string eventType, string summary, string? targetType = null, Guid? targetId = null,
            Guid? clientId = null, string? details = null, string? actorEmailOverride = null,
            Guid? actorUserIdOverride = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordSystemAsync(
            string eventType, string summary, string? details = null, Guid? clientId = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
