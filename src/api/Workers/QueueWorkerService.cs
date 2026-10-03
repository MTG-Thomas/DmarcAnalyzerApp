using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Analytics.Spf;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Maintenance;
using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Application.Retention;
using System.Text.Json;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Workers;

/// <summary>
/// The worker loop: every interval it drains durable manual sync requests,
/// syncs each active polled source in turn, then runs the periodic passes
/// (DNS refresh, MTA-STS checks, alerts, digest, database retention, mailbox
/// retention, backup offload) when they come due. One worker per database,
/// enforced by <see cref="WorkerSingleInstanceLock"/> while
/// <c>Worker:EnforceSingleInstance</c> is on (the default).
/// </summary>
public sealed class QueueWorkerService(
    IServiceScopeFactory scopeFactory,
    WorkerSingleInstanceLock workerLock,
    IOptions<WorkerOptions> options,
    IOptions<BackupOptions> backupOptions,
    ILogger<QueueWorkerService> logger) : BackgroundService, IWorkerPassSource
{
    private readonly WorkerOptions _options = options.Value;
    private readonly BackupOptions _backupOptions = backupOptions.Value;

    private const int MinDelaySeconds = 15;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait in the background, not host startup: a rolling replacement must
        // serve API probes before the platform stops the previous worker. No
        // ingestion or scheduled side effect may run until that owner exits.
        await workerLock.AcquireAsync(stoppingToken);
        logger.LogInformation("Queue worker started.");

        // Everything runs inside the loop: a pass that throws (database not
        // reachable, schema not migrated yet) must be caught and retried, not
        // allowed to escape. An exception out of ExecuteAsync stops the whole
        // host, which for worker mode means ingestion silently stops.
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                logger.LogError(ex, "Worker scheduler pass failed ({Failures} consecutive)", consecutiveFailures);
            }

            try
            {
                await Task.Delay(NextDelay(consecutiveFailures, _options.ScheduleIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Queue worker stopping.");
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkerPass> GetPasses() =>
    [
        new("stale-sync-close", CloseStaleRunningSyncsAsync),
        new("sync-request-drain", RunSyncRequestDrainPassAsync),
        new("scheduled-sync", RunScheduledSyncPassAsync),
        new("alerts", RunAlertPassIfDueAsync),
        new("digest", RunDigestPassIfDueAsync),
        new("retention", RunRetentionPassIfDueAsync),
        new("dns-refresh", RunDnsRefreshPassIfDueAsync),
        new("mta-sts", RunMtaStsPassIfDueAsync),
        new("spf-drift", RunSpfDriftPassIfDueAsync),
        new("backup-offload", RunBackupOffloadPassIfDueAsync),
        new("mailbox-retention", RunMailboxRetentionPassIfDueAsync),
    ];

    /// <summary>
    /// One full iteration: every pass in order. A throw aborts the remaining
    /// passes and propagates to the caller — the loop counts it as a failed
    /// iteration and retries. Callers that must not let one pass skip the
    /// others run <see cref="GetPasses"/> with their own isolation instead.
    /// </summary>
    public async Task RunIterationAsync(CancellationToken ct)
    {
        foreach (var pass in GetPasses())
        {
            await pass.Run(ct);
        }
    }

    /// <summary>
    /// Delay before the next pass. Healthy passes wait the configured interval;
    /// after a failure it retries far sooner and backs off exponentially
    /// (5s, 10s, 20s, …) up to that interval — so a worker that starts before
    /// the database is ready recovers in seconds instead of idling for the full
    /// production hour.
    /// </summary>
    public static TimeSpan NextDelay(int consecutiveFailures, int intervalSeconds)
    {
        var normalSeconds = Math.Max(MinDelaySeconds, intervalSeconds);
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.FromSeconds(normalSeconds);
        }

        var backoffSeconds = 5L << Math.Min(consecutiveFailures - 1, 10);
        return TimeSpan.FromSeconds(Math.Min(backoffSeconds, normalSeconds));
    }

    /// <summary>
    /// Evaluates alert rules on their own cadence (<c>Alerts:IntervalMinutes</c>).
    /// Separate from the sync interval because reports arrive daily — evaluating
    /// far more often than that only risks duplicate work, and the cooldown in the
    /// evaluation service is what actually prevents repeat notifications.
    /// </summary>
    private async Task RunAlertPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var alertOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<AlertOptions>>().Value;

        if (!alertOptions.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, alertOptions.IntervalMinutes));
        var tasks = new ScheduledTaskService(
            scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        if (!await tasks.IsDueAsync(ScheduledTaskKeys.Alert, interval, ct))
        {
            return;
        }

        var alerts = scope.ServiceProvider.GetRequiredService<IAlertEvaluationService>();
        await alerts.EvaluateAsync(ct);

        // Only on success, so a failure retries next pass.
        await tasks.RecordRunAsync(ScheduledTaskKeys.Alert, success: true, ct);
    }

    /// <summary>
    /// Keeps each domain's cached DMARC policy fresh so list views can render the real
    /// policy from one query. Detail-page views correct individual domains as a side
    /// effect of the lookup they already make; this pass is what covers the domains
    /// nobody opens — including the ones that stopped reporting, which are exactly the
    /// ones whose policy would otherwise be silently wrong.
    /// </summary>
    private async Task RunDnsRefreshPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dnsOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<DnsOptions>>().Value;

        if (!dnsOptions.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, dnsOptions.RefreshIntervalHours));
        var tasks = new ScheduledTaskService(
            scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        if (!await tasks.IsDueAsync(ScheduledTaskKeys.DnsRefresh, interval, ct))
        {
            return;
        }

        var cache = scope.ServiceProvider.GetRequiredService<IDnsPolicyCache>();
        await cache.RefreshAllAsync(ct);

        // Only on success, so a failure retries next pass.
        await tasks.RecordRunAsync(ScheduledTaskKeys.DnsRefresh, success: true, ct);
    }

    /// <summary>
    /// Checks a few times a day whether the monthly digest is due. The real
    /// guard against duplicates is the unique (client, period) row the digest
    /// service writes, so a restart or an extra check is harmless.
    /// </summary>
    private async Task RunDigestPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DigestOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, options.CheckIntervalHours));
        var tasks = new ScheduledTaskService(
            scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        if (!await tasks.IsDueAsync(ScheduledTaskKeys.Digest, interval, ct))
        {
            return;
        }

        var digest = scope.ServiceProvider.GetRequiredService<IDigestService>();
        await digest.SendDueAsync(ct);
        await tasks.RecordRunAsync(ScheduledTaskKeys.Digest, success: true, ct);
    }

    /// <summary>
    /// Enforces per-client retention. Runs on its own slow cadence
    /// (<c>Worker:RetentionIntervalHours</c>, daily by default) rather than every
    /// sync pass — retention is measured in months, so there is nothing to gain
    /// from checking hourly. The cadence is durable, so a restart does not run
    /// it again until the interval has elapsed; purging is idempotent anyway.
    /// </summary>
    private async Task RunRetentionPassIfDueAsync(CancellationToken ct)
    {
        if (!_options.RetentionEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.RetentionIntervalHours));
        using var scope = scopeFactory.CreateScope();
        var tasks = new ScheduledTaskService(
            scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        if (!await tasks.IsDueAsync(ScheduledTaskKeys.Retention, interval, ct))
        {
            return;
        }

        var retention = scope.ServiceProvider.GetRequiredService<IRetentionPurgeService>();
        await retention.PurgeAsync(dryRun: false, _options.RetentionBatchSize, ct);

        // Only mark it done on success, so a failure retries on the next pass
        // instead of waiting out the whole interval.
        await tasks.RecordRunAsync(ScheduledTaskKeys.Retention, success: true, ct);
    }

    /// <summary>
    /// Keeps each domain's MTA-STS state fresh: the `_mta-sts` TXT record, the
    /// policy fetch, and the MX cross-check. The alert pass reads what this
    /// writes, so this is also what makes an id change or a broken policy host
    /// visible without anyone opening the domain.
    /// <para>
    /// Swallows its own exceptions, like the backup pass below and for the same
    /// structural reason: it talks to third parties (every client's DNS and
    /// policy host), which makes it likelier to fail than the passes after it —
    /// and per-domain failures are already absorbed inside the refresh, so an
    /// exception here means the pass itself broke, not a domain.
    /// </para>
    /// </summary>
    private async Task RunMtaStsPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var mtaStsOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<MtaStsOptions>>().Value;

        if (!mtaStsOptions.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, mtaStsOptions.CheckIntervalHours));
        var tasks = new ScheduledTaskService(
            scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        if (!await tasks.IsDueAsync(ScheduledTaskKeys.MtaSts, interval, ct))
        {
            return;
        }

        try
        {
            var cache = scope.ServiceProvider.GetRequiredService<IMtaStsStateCache>();
            await cache.RefreshAllAsync(ct);
            await tasks.RecordRunAsync(ScheduledTaskKeys.MtaSts, success: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MTA-STS check pass failed; ingestion is unaffected");
            await tasks.RecordRunAsync(ScheduledTaskKeys.MtaSts, success: false, ct);
        }
    }

    /// <summary>
    /// Keeps each domain's SPF drift state fresh: the live record, the
    /// dependency snapshot, and the candidate it yields. The alert pass reads
    /// what this writes, so this is also what surfaces a provider-side change
    /// without anyone opening the domain.
    /// <para>
    /// Swallows its own exceptions, like the MTA-STS pass above and for the
    /// same structural reason: it talks to third parties (every dependency's
    /// DNS), which makes it likelier to fail than the passes after it — and
    /// per-domain failures are already absorbed inside the refresh, so an
    /// exception here means the pass itself broke, not a domain.
    /// </para>
    /// </summary>
    private async Task RunSpfDriftPassIfDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var driftOptions = scope.ServiceProvider
            .GetRequiredService<IOptions<SpfDriftOptions>>().Value;

        if (!driftOptions.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, driftOptions.CheckIntervalHours));
        var tasks = new ScheduledTaskService(
            scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        if (!await tasks.IsDueAsync(ScheduledTaskKeys.SpfDrift, interval, ct))
        {
            return;
        }

        try
        {
            var cache = scope.ServiceProvider.GetRequiredService<ISpfDriftStateCache>();
            await cache.RefreshAllAsync(ct);
            await tasks.RecordRunAsync(ScheduledTaskKeys.SpfDrift, success: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SPF drift check pass failed; ingestion is unaffected");
            await tasks.RecordRunAsync(ScheduledTaskKeys.SpfDrift, success: false, ct);
        }
    }

    /// <summary>
    /// Ships the configuration snapshot and any new history rows to object storage.
    /// <para>
    /// Runs last, and swallows its own exceptions, for a structural reason: all six passes
    /// share one try/catch in the loop above, so a pass that throws skips every pass after
    /// it. Backup depends on a third party being reachable — a bucket, over the network —
    /// which makes it the pass most likely to fail, and the least acceptable one to let
    /// stop ingestion. Its own failures are recorded in <c>backup_stream_state</c> and
    /// surfaced in the console instead.
    /// </para>
    /// <para>
    /// Interval resolution is <c>Worker:ScheduleIntervalSeconds</c>, like every other gate
    /// here: with the shipped hourly schedule, a 30-minute backup interval means roughly
    /// hourly. Not floored to an hour, though, because a shortened schedule interval should
    /// actually deliver the configured cadence.
    /// </para>
    /// </summary>
    private async Task RunBackupOffloadPassIfDueAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _backupOptions.IntervalMinutes));
        using var scope = scopeFactory.CreateScope();
        var tasks = new ScheduledTaskService(
            scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        if (!await tasks.IsDueAsync(ScheduledTaskKeys.BackupOffload, interval, ct))
        {
            return;
        }

        try
        {
            var offload = scope.ServiceProvider.GetRequiredService<IBackupOffloadService>();
            var result = await offload.RunAsync(ct);

            // A pass that did nothing because no bucket is configured must not start the
            // clock, or enabling offload later would wait out a whole interval.
            if (result.Ran)
            {
                await tasks.RecordRunAsync(ScheduledTaskKeys.BackupOffload, success: true, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup offload pass failed; ingestion is unaffected");
            await tasks.RecordRunAsync(ScheduledTaskKeys.BackupOffload, success: false, ct);
        }
    }

    /// <summary>
    /// Deletes report mail that has aged past the retention window, so the mailbox stops
    /// being an unbounded second copy of data the database has already purged.
    /// <para>
    /// Runs last and swallows its own exceptions, like the offload pass above and for the
    /// same structural reason. Every source is opt-in, so on a default install this pass
    /// connects to nothing at all — the planner suspends each source before any mailbox is
    /// opened.
    /// </para>
    /// </summary>
    private async Task RunMailboxRetentionPassIfDueAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _options.MailboxRetentionIntervalHours));
        using var scope = scopeFactory.CreateScope();
        var tasks = new ScheduledTaskService(
            scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>());
        if (!await tasks.IsDueAsync(ScheduledTaskKeys.MailboxRetention, interval, ct))
        {
            return;
        }

        try
        {
            var retention = scope.ServiceProvider.GetRequiredService<IMailboxRetentionService>();
            await retention.RunAsync(dryRun: false, ct);

            await tasks.RecordRunAsync(ScheduledTaskKeys.MailboxRetention, success: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mailbox retention pass failed; ingestion is unaffected");
            await tasks.RecordRunAsync(ScheduledTaskKeys.MailboxRetention, success: false, ct);
        }
    }

    private async Task RunScheduledSyncPassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();

        var activeReportSources = await db.ReportSources
            .AsNoTracking()
            .Where(x => x.IsActive && ReportSourceProtocols.Polled.Contains(x.Protocol))
            .Select(x => x.Id)
            .ToListAsync(ct);

        if (activeReportSources.Count == 0)
        {
            logger.LogDebug("No active polled report sources found for scheduled pass");
            return;
        }

        logger.LogInformation("Scheduled sync pass for {Count} report sources", activeReportSources.Count);

        foreach (var reportSourceId in activeReportSources)
        {
            try
            {
                var result = await ExecuteWithRetryAsync(reportSourceId, ct);

                if (!result.IsSuccess)
                {
                    logger.LogInformation(
                        "Scheduled sync failed to start for report source {ReportSourceId}: {Error}",
                        reportSourceId,
                        result.Error);
                    continue;
                }

                var value = result.Value!;
                if (!value.Success)
                {
                    logger.LogWarning(
                        "Scheduled sync failed for report source {ReportSourceId}: {Error}",
                        reportSourceId,
                        value.Error);
                    continue;
                }

                logger.LogInformation(
                    "Scheduled sync completed for report source {ReportSourceId}. Messages={MessagesScanned}, Attachments={AttachmentsProcessed}, Inserted={ReportsInserted}, Duplicates={ReportsSkippedAsDuplicate}, ParseFailures={ParseFailures}",
                    reportSourceId,
                    value.MessagesScanned,
                    value.AttachmentsProcessed,
                    value.ReportsInserted,
                    value.ReportsSkippedAsDuplicate,
                    value.ParseFailures);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogDebug("Scheduled sync cancelled for report source {ReportSourceId}", reportSourceId);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled sync crashed for report source {ReportSourceId}", reportSourceId);
            }
        }
    }

    private async Task CloseStaleRunningSyncsAsync(CancellationToken ct)
    {
        var staleRunTimeoutMinutes = Math.Max(5, _options.StaleRunTimeoutMinutes);
        var staleBeforeUtc = DateTime.UtcNow.AddMinutes(-staleRunTimeoutMinutes);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();

        var staleRuns = await db.MailboxSyncRuns
            .Where(x => x.Status == "running" && x.StartedAtUtc < staleBeforeUtc)
            .ToListAsync(ct);

        if (staleRuns.Count == 0)
        {
            return;
        }

        foreach (var staleRun in staleRuns)
        {
            staleRun.Status = "failed";
            staleRun.FinishedAtUtc = DateTime.UtcNow;
            staleRun.Error = string.IsNullOrWhiteSpace(staleRun.Error)
                ? $"auto-closed stale running sync after {staleRunTimeoutMinutes} minutes"
                : staleRun.Error;
        }

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Auto-closed {Count} stale running mailbox sync runs older than {TimeoutMinutes} minutes",
            staleRuns.Count,
            staleRunTimeoutMinutes);
    }

    /// <summary>
    /// Drains durable manual sync requests: claims each queued row, runs one
    /// sync, writes the outcome back. Always due — queued work is explicit
    /// demand, and an empty queue costs one indexed read. Manual requests run
    /// before the scheduled pass so an operator's explicit demand wins.
    /// <para>
    /// Single attempt per request per pass, no retry: transient trouble is
    /// retried by the scheduled pass over the same backlog (and the operator
    /// can always re-enqueue), while backoff sleeps would blow the once-mode
    /// bound. Cancellation marks the in-flight request partial — checkpoints
    /// survive — and propagates.
    /// </para>
    /// </summary>
    private async Task RunSyncRequestDrainPassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var requests = scope.ServiceProvider.GetRequiredService<ISyncRequestService>();

        var recovered = await requests.RequeueStaleAsync(
            TimeSpan.FromMinutes(Math.Max(5, _options.StaleRunTimeoutMinutes)), ct);
        if (recovered > 0)
        {
            logger.LogWarning("Recovered {Count} abandoned manual sync requests", recovered);
        }

        // Bounded per pass: worker-once must terminate, and one chatty source
        // must not starve the passes after this one.
        const int maxClaimsPerPass = 25;
        for (var claimed = 0; claimed < maxClaimsPerPass; claimed++)
        {
            var claim = await requests.ClaimNextAsync(ct);
            if (claim is null)
            {
                return;
            }

            await RunClaimedSyncAsync(requests, claim, ct);
        }

        logger.LogWarning(
            "Sync request drain hit its {Max} per-pass cap; the remainder waits for the next pass",
            maxClaimsPerPass);
    }

    private async Task RunClaimedSyncAsync(
        ISyncRequestService requests, SyncRequestClaim claim, CancellationToken ct)
    {
        using var syncScope = scopeFactory.CreateScope();
        var syncService = syncScope.ServiceProvider.GetRequiredService<IMailboxSyncService>();
        try
        {
            var result = await syncService.SyncReportSourceAsync(claim.ReportSourceId, ct);
            if (!result.IsSuccess)
            {
                await requests.FailAsync(claim.RequestId, result.Error ?? "sync failed to start", null, ct);
                return;
            }

            var value = result.Value!;
            if (value.Success)
            {
                await requests.CompleteAsync(claim.RequestId, SerializeOutcome(value), ct);
            }
            else
            {
                await requests.FailAsync(claim.RequestId, value.Error ?? "sync failed", SerializeOutcome(value), ct);
            }
        }
        catch (OperationCanceledException)
        {
            await requests.MarkPartialAsync(claim.RequestId, null, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual sync crashed for request {RequestId}", claim.RequestId);
            await requests.FailAsync(claim.RequestId, "sync crashed: " + ex.Message, null, ct);
        }
    }

    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The counters the status endpoint renders — the same numbers the
    /// scheduled pass logs, as camelCase JSON the console iterates.
    /// </summary>
    private static string SerializeOutcome(MailboxSyncResult value)
        => JsonSerializer.Serialize(new
        {
            value.MessagesScanned,
            value.AttachmentsProcessed,
            value.ReportsInserted,
            value.ReportsSkippedAsDuplicate,
            value.TlsReportsInserted,
            value.TlsReportsSkippedAsDuplicate,
            value.ParseFailures,
        }, OutcomeJson);

    private async Task<ServiceResult<MailboxSyncResult>> ExecuteWithRetryAsync(Guid reportSourceId, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _options.MaxRetryAttempts);
        var baseDelay = Math.Max(1, _options.RetryBaseDelaySeconds);
        ServiceResult<MailboxSyncResult>? lastResult = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var syncScope = scopeFactory.CreateScope();
            var syncService = syncScope.ServiceProvider.GetRequiredService<IMailboxSyncService>();
            var result = await syncService.SyncReportSourceAsync(reportSourceId, "scheduled", ct);
            lastResult = result;

            if (!result.IsSuccess)
            {
                return result;
            }

            if (result.Value?.Success == true)
            {
                return result;
            }

            if (attempt == maxAttempts)
            {
                return result;
            }

            var delay = TimeSpan.FromSeconds(baseDelay * Math.Pow(2, attempt - 1));
            logger.LogWarning(
                "Scheduled sync attempt {Attempt}/{MaxAttempts} failed for report source {ReportSourceId}. Retrying in {DelaySeconds}s",
                attempt,
                maxAttempts,
                reportSourceId,
                (int)delay.TotalSeconds);

            await Task.Delay(delay, ct);
        }

        return lastResult ?? ServiceResult<MailboxSyncResult>.Failure("retry pipeline returned no result", 500);
    }
}
