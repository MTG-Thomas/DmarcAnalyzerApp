using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        null, null, null, null, null, false, null, null,
        DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task RoutesReturnServiceStatuses()
    {
        var sources = new StubReportSourceService();
        var syncRequests = new StubSyncRequestService();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IReportSourceService>(sources);
        builder.Services.AddSingleton<ISyncRequestService>(syncRequests);
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

        syncRequests.EnqueueResult = ServiceResult<SyncRequestEnqueueResult>.Failure("not found", 404);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(
            $"/api/v1/report-sources/{SourceId}/sync", null)).StatusCode);
        syncRequests.EnqueueResult = ServiceResult<SyncRequestEnqueueResult>.Failure("mailbox source configuration is incomplete", 409);
        var conflict = await client.PostAsync($"/api/v1/report-sources/{SourceId}/sync", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // A fresh enqueue answers 202 with the request and where to poll it.
        var requestId = Guid.NewGuid();
        syncRequests.EnqueueResult = ServiceResult<SyncRequestEnqueueResult>.Success(
            new SyncRequestEnqueueResult(requestId, SyncRequestStatus.Queued, IsNew: true));
        var accepted = await client.PostAsync($"/api/v1/report-sources/{SourceId}/sync", null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(requestId.ToString(), acceptedBody.GetProperty("requestId").GetString());
        Assert.Equal(SyncRequestStatus.Queued, acceptedBody.GetProperty("status").GetString());
        Assert.Equal($"/api/v1/report-sources/sync-requests/{requestId}",
            acceptedBody.GetProperty("statusUrl").GetString());

        // A source with a request already in flight answers 200 with that
        // same request in the same shape — including when it is running.
        syncRequests.EnqueueResult = ServiceResult<SyncRequestEnqueueResult>.Success(
            new SyncRequestEnqueueResult(requestId, SyncRequestStatus.Running, IsNew: false));
        var deduped = await client.PostAsync($"/api/v1/report-sources/{SourceId}/sync", null);
        Assert.Equal(HttpStatusCode.OK, deduped.StatusCode);
        var dedupedBody = await deduped.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(requestId.ToString(), dedupedBody.GetProperty("requestId").GetString());
        Assert.Equal(SyncRequestStatus.Running, dedupedBody.GetProperty("status").GetString());
        Assert.Equal($"/api/v1/report-sources/sync-requests/{requestId}",
            dedupedBody.GetProperty("statusUrl").GetString());

        syncRequests.GetResult = ServiceResult<SyncRequestDetails>.Failure("not found", 404);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            $"/api/v1/report-sources/sync-requests/{requestId}")).StatusCode);

        var finishedAtUtc = DateTime.UtcNow;
        using var summaryDoc = JsonDocument.Parse("""{"reportsInserted":3}""");
        syncRequests.GetResult = ServiceResult<SyncRequestDetails>.Success(new SyncRequestDetails(
            requestId, SourceId, SyncRequestStatus.Completed, finishedAtUtc.AddMinutes(-2),
            finishedAtUtc.AddMinutes(-2), finishedAtUtc, 1, null, summaryDoc.RootElement.Clone()));
        var status = await client.GetAsync($"/api/v1/report-sources/sync-requests/{requestId}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var statusBody = await status.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(requestId.ToString(), statusBody.GetProperty("requestId").GetString());
        Assert.Equal(SourceId.ToString(), statusBody.GetProperty("reportSourceId").GetString());
        Assert.Equal(SyncRequestStatus.Completed, statusBody.GetProperty("status").GetString());
        Assert.Equal(1, statusBody.GetProperty("attempts").GetInt32());
        Assert.Equal(3, statusBody.GetProperty("summary").GetProperty("reportsInserted").GetInt32());
    }

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

    private sealed class StubSyncRequestService : ISyncRequestService
    {
        public ServiceResult<SyncRequestEnqueueResult> EnqueueResult { get; set; } =
            ServiceResult<SyncRequestEnqueueResult>.Success(
                new SyncRequestEnqueueResult(Guid.NewGuid(), SyncRequestStatus.Queued, IsNew: true));

        public ServiceResult<SyncRequestDetails> GetResult { get; set; } =
            ServiceResult<SyncRequestDetails>.Failure("not found", 404);

        public Task<ServiceResult<SyncRequestEnqueueResult>> EnqueueAsync(Guid reportSourceId, CancellationToken ct)
            => Task.FromResult(EnqueueResult);

        public Task<ServiceResult<SyncRequestDetails>> GetAsync(Guid requestId, CancellationToken ct)
            => Task.FromResult(GetResult);

        public Task<SyncRequestClaim?> ClaimNextAsync(CancellationToken ct)
            => Task.FromResult<SyncRequestClaim?>(null);

        public Task<bool> CompleteAsync(Guid requestId, string? resultJson, CancellationToken ct)
            => Task.FromResult(false);

        public Task<bool> MarkPartialAsync(Guid requestId, string? resultJson, CancellationToken ct)
            => Task.FromResult(false);

        public Task<bool> FailAsync(Guid requestId, string error, string? resultJson, CancellationToken ct)
            => Task.FromResult(false);

        public Task<bool> HeartbeatAsync(Guid requestId, string? progressJson, CancellationToken ct)
            => Task.FromResult(false);

        public Task<int> RequeueStaleAsync(TimeSpan staleAfter, CancellationToken ct)
            => Task.FromResult(0);
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
