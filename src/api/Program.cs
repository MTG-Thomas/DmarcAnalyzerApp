using Carter;
using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Analytics.Spf;
using DmarcAnalyzer.Api.Application.ApiSources;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Hosting;
using DmarcAnalyzer.Api.Application.Observability;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.MagicLinks;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Application.Retention;
using DmarcAnalyzer.Api.Application.ReportSources;
using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Application.Users;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Middleware;
using DmarcAnalyzer.Api.Modules;
using DmarcAnalyzer.Api.Workers;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

// Throws on an unrecognised value rather than defaulting to api — see AppRuntimeMode.
var mode = AppRuntimeMode.FromEnvironment();

if (mode == AppMode.Migrate)
{
    // Deliberately the smallest host that can migrate: a DbContext and the audit
    // trail, nothing that serves or ingests. It runs to completion and exits, so
    // an orchestrator can order schema changes ahead of every application pod.
    var migrateBuilder = Host.CreateApplicationBuilder(args);
    // A value that cannot be converted to the type it is bound to fails here,
    // as one sentence naming the variable, rather than as an unhandled binder
    // exception somewhere later. A mistyped variable *name* is a different
    // problem and this cannot see it — nothing binds it, so the default applies.
    ConfigurationPreflight.Validate(migrateBuilder.Configuration);
    var migrateTelemetry = migrateBuilder.AddTelemetry(mode);
    var migrateConnectionString = ConnectionStringResolver.Resolve(migrateBuilder.Configuration)
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Default or DATABASE_URL is required in migrate mode.");

    migrateBuilder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
        options.UseNpgsql(migrateConnectionString));
    migrateBuilder.Services.AddHttpContextAccessor();
    migrateBuilder.Services.AddScoped<ICurrentUserContext, SystemUserContext>();
    migrateBuilder.Services.AddScoped<IAuditLog, AuditLog>();

    using var migrateHost = migrateBuilder.Build();
    using var scope = migrateHost.Services.CreateScope();
    var migrateDb = scope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();
    var migrateLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>().CreateLogger("Migrate");
    migrateLogger.LogTelemetryStatus(migrateTelemetry);

    // The same budget the other two paths use. AddDmarcReportRecordRangeBegin
    // rewrites 5.3M rows in ~94s on a production-sized database, well past
    // Npgsql's 30s default.
    migrateDb.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

    var pendingMigrations = (await migrateDb.Database.GetPendingMigrationsAsync()).ToArray();

    if (pendingMigrations.Length == 0)
    {
        // Not an error. Re-running an unchanged release must be a no-op, or every
        // upgrade would need a human to decide whether the Job was meant to fail.
        migrateLogger.LogInformation("No pending migrations; nothing to do.");
        return;
    }

    migrateLogger.LogInformation(
        "Applying {Count} pending migration(s): {Migrations}",
        pendingMigrations.Length,
        string.Join(", ", pendingMigrations));

    await migrateDb.Database.MigrateAsync();

    await scope.ServiceProvider.GetRequiredService<IAuditLog>().RecordSystemAsync(
        AuditEvents.DatabaseMigrated,
        $"Applied {pendingMigrations.Length} pending database migration" +
        $"{(pendingMigrations.Length == 1 ? "" : "s")} via migrate mode",
        details: string.Join(", ", pendingMigrations));

    migrateLogger.LogInformation("Migrations applied.");
    return;
}

if (mode == AppMode.Worker)
{
    var workerBuilder = Host.CreateApplicationBuilder(args);
    ConfigurationPreflight.Validate(workerBuilder.Configuration);
    var workerTelemetry = workerBuilder.AddTelemetry(mode);
    var workerConnectionString = ConnectionStringResolver.Resolve(workerBuilder.Configuration)
        ?? "Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres";

    workerBuilder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
        options.UseNpgsql(workerConnectionString));
    workerBuilder.Services.AddCredentialProtection(workerBuilder.Configuration);
    workerBuilder.Services.AddScoped<IDmarcReportParser, DmarcRuaReportParser>();
    workerBuilder.Services.AddScoped<ITlsRptReportParser, TlsRptReportParser>();
    workerBuilder.Services.AddScoped<IDomainIngestResolver, DomainIngestResolver>();
    workerBuilder.Services.AddScoped<IDmarcReportIngestor, DmarcReportIngestor>();
    workerBuilder.Services.AddScoped<ITlsReportIngestor, TlsReportIngestor>();
    // One transport per protocol, resolved by name rather than branched on. Both are
    // registered wherever a sync can be triggered, so a manual sync from the console and
    // the worker's scheduled pass reach the same code.
    workerBuilder.Services.AddSingleton<IPolledSourceTransport, ImapMailboxTransport>();
    workerBuilder.Services.AddSingleton<IPolledSourceTransport, Pop3MailboxTransport>();
    workerBuilder.Services.AddSingleton<IPolledSourceTransportFactory, PolledSourceTransportFactory>();
    workerBuilder.Services.AddSingleton<IPolledSourceTransport, S3ReportSourceTransport>();
    workerBuilder.Services.AddScoped<IMailboxSyncService, MailboxSyncService>();
    workerBuilder.Services.AddScoped<ISyncRequestService, SyncRequestService>();
    workerBuilder.Services.AddHttpContextAccessor();
    workerBuilder.Services.AddScoped<ICurrentUserContext, SystemUserContext>();
    workerBuilder.Services.AddScoped<IAuditLog, AuditLog>();
    workerBuilder.Services.Configure<RetentionOptions>(workerBuilder.Configuration.GetSection("Retention"));
    workerBuilder.Services.AddScoped<IRetentionPurgeService, RetentionPurgeService>();
    workerBuilder.Services.Configure<EmailOptions>(workerBuilder.Configuration.GetSection("Email"));
    workerBuilder.Services.Configure<AlertOptions>(workerBuilder.Configuration.GetSection("Alerts"));
    workerBuilder.Services.AddScoped<IEmailSender, EmailSender>();
    workerBuilder.Services.Configure<DigestOptions>(workerBuilder.Configuration.GetSection("Digest"));
    workerBuilder.Services.AddScoped<IAlertEvaluationService, AlertEvaluationService>();
    workerBuilder.Services.AddScoped<IDigestService, DigestService>();
    // DnsTxtResolver caches lookups in IMemoryCache; the worker host has to provide
    // it too, not just the API host.
    workerBuilder.Services.AddMemoryCache();
    workerBuilder.Services.Configure<DnsOptions>(workerBuilder.Configuration.GetSection("Dns"));
    workerBuilder.Services.AddSingleton<IAuthoritativeDnsClientLocator, AuthoritativeDnsClientLocator>();
    workerBuilder.Services.AddSingleton<IDnsTxtResolver, DnsTxtResolver>();
    workerBuilder.Services.AddScoped<IDmarcPolicyResolver, DmarcPolicyResolver>();
    workerBuilder.Services.AddScoped<IDnsPolicyCache, DnsPolicyCache>();
    workerBuilder.Services.AddMtaStsMonitoring(workerBuilder.Configuration);
    workerBuilder.Services.AddSpfDriftMonitoring(workerBuilder.Configuration);
    workerBuilder.Services.Configure<WorkerOptions>(workerBuilder.Configuration.GetSection("Worker"));
    workerBuilder.Services.AddOptions<ReportPayloadExtractionOptions>()
        .Bind(workerBuilder.Configuration.GetSection(ReportPayloadExtractionOptions.SectionName))
        .Validate(options => options.IsValid(), "Ingestion limits must be positive; MaxEntryBytes must not exceed MaxExpandedBytes; MaxCompressionRatio must be at least 1")
        .ValidateOnStart();
    workerBuilder.Services.AddSingleton<IReportPayloadExtractor, BoundedReportPayloadExtractor>();
    workerBuilder.Services.AddScoped<IReportPayloadIngestor, ReportPayloadIngestor>();
    // Backup offload runs on the loop, so the worker host needs the whole chain — the
    // export service included. Registering it only on the API host would leave the pass
    // throwing from GetRequiredService in worker mode, visible as nothing but a caught log
    // line while backups silently never happen.
    workerBuilder.Services.Configure<BackupOptions>(workerBuilder.Configuration.GetSection("Backup"));
    workerBuilder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
    workerBuilder.Services.AddScoped<IReportMailArchive, ReportMailArchive>();
    workerBuilder.Services.AddScoped<IBackupExportService, BackupExportService>();
    workerBuilder.Services.AddScoped<IBackupOffloadService, BackupOffloadService>();
    workerBuilder.Services.AddScoped<IMailboxRetentionPlanner, MailboxRetentionPlanner>();
    workerBuilder.Services.AddScoped<IMailboxRetentionService, MailboxRetentionService>();
    // The loop waits for ownership in the background before any ingestion.
    workerBuilder.Services.AddSingleton<WorkerSingleInstanceLock>();
    workerBuilder.Services.AddHostedService<QueueWorkerService>();

    var workerHost = workerBuilder.Build();
    workerHost.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Telemetry").LogTelemetryStatus(workerTelemetry);
    await workerHost.RunAsync();
    return;
}

if (mode == AppMode.WorkerOnce)
{
    // One iteration of the worker loop, then exit: the same passes over the
    // same services as the worker block above, driven once by WorkerOnceRunner
    // instead of forever by the hosted service. The two registrations must stay
    // in step — a pass that resolves a service missing here throws only at
    // runtime, and only in this mode.
    var onceBuilder = Host.CreateApplicationBuilder(args);
    ConfigurationPreflight.Validate(onceBuilder.Configuration);
    var onceTelemetry = onceBuilder.AddTelemetry(mode);
    var onceConnectionString = ConnectionStringResolver.Resolve(onceBuilder.Configuration)
        ?? "Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres";

    onceBuilder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
        options.UseNpgsql(onceConnectionString));
    onceBuilder.Services.AddCredentialProtection(onceBuilder.Configuration);
    onceBuilder.Services.AddScoped<IDmarcReportParser, DmarcRuaReportParser>();
    onceBuilder.Services.AddScoped<ITlsRptReportParser, TlsRptReportParser>();
    onceBuilder.Services.AddScoped<IDomainIngestResolver, DomainIngestResolver>();
    onceBuilder.Services.AddScoped<IDmarcReportIngestor, DmarcReportIngestor>();
    onceBuilder.Services.AddScoped<ITlsReportIngestor, TlsReportIngestor>();
    onceBuilder.Services.AddSingleton<IPolledSourceTransport, ImapMailboxTransport>();
    onceBuilder.Services.AddSingleton<IPolledSourceTransport, Pop3MailboxTransport>();
    onceBuilder.Services.AddSingleton<IPolledSourceTransportFactory, PolledSourceTransportFactory>();
    onceBuilder.Services.AddSingleton<IPolledSourceTransport, S3ReportSourceTransport>();
    onceBuilder.Services.AddScoped<IMailboxSyncService, MailboxSyncService>();
    onceBuilder.Services.AddScoped<ISyncRequestService, SyncRequestService>();
    onceBuilder.Services.AddHttpContextAccessor();
    onceBuilder.Services.AddScoped<ICurrentUserContext, SystemUserContext>();
    onceBuilder.Services.AddScoped<IAuditLog, AuditLog>();
    onceBuilder.Services.Configure<RetentionOptions>(onceBuilder.Configuration.GetSection("Retention"));
    onceBuilder.Services.AddScoped<IRetentionPurgeService, RetentionPurgeService>();
    onceBuilder.Services.Configure<EmailOptions>(onceBuilder.Configuration.GetSection("Email"));
    onceBuilder.Services.Configure<AlertOptions>(onceBuilder.Configuration.GetSection("Alerts"));
    onceBuilder.Services.AddScoped<IEmailSender, EmailSender>();
    onceBuilder.Services.Configure<DigestOptions>(onceBuilder.Configuration.GetSection("Digest"));
    onceBuilder.Services.AddScoped<IAlertEvaluationService, AlertEvaluationService>();
    onceBuilder.Services.AddScoped<IDigestService, DigestService>();
    onceBuilder.Services.AddMemoryCache();
    onceBuilder.Services.Configure<DnsOptions>(onceBuilder.Configuration.GetSection("Dns"));
    onceBuilder.Services.AddSingleton<IAuthoritativeDnsClientLocator, AuthoritativeDnsClientLocator>();
    onceBuilder.Services.AddSingleton<IDnsTxtResolver, DnsTxtResolver>();
    onceBuilder.Services.AddScoped<IDmarcPolicyResolver, DmarcPolicyResolver>();
    onceBuilder.Services.AddScoped<IDnsPolicyCache, DnsPolicyCache>();
    onceBuilder.Services.AddMtaStsMonitoring(onceBuilder.Configuration);
    onceBuilder.Services.AddSpfDriftMonitoring(onceBuilder.Configuration);
    onceBuilder.Services.Configure<WorkerOptions>(onceBuilder.Configuration.GetSection("Worker"));
    onceBuilder.Services.Configure<WorkerOnceOptions>(onceBuilder.Configuration.GetSection(WorkerOnceOptions.SectionName));
    onceBuilder.Services.AddOptions<ReportPayloadExtractionOptions>()
        .Bind(onceBuilder.Configuration.GetSection(ReportPayloadExtractionOptions.SectionName))
        .Validate(options => options.IsValid(), "Ingestion limits must be positive; MaxEntryBytes must not exceed MaxExpandedBytes; MaxCompressionRatio must be at least 1")
        .ValidateOnStart();
    onceBuilder.Services.AddSingleton<IReportPayloadExtractor, BoundedReportPayloadExtractor>();
    onceBuilder.Services.AddScoped<IReportPayloadIngestor, ReportPayloadIngestor>();
    onceBuilder.Services.Configure<BackupOptions>(onceBuilder.Configuration.GetSection("Backup"));
    onceBuilder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
    onceBuilder.Services.AddScoped<IReportMailArchive, ReportMailArchive>();
    onceBuilder.Services.AddScoped<IBackupExportService, BackupExportService>();
    onceBuilder.Services.AddScoped<IBackupOffloadService, BackupOffloadService>();
    onceBuilder.Services.AddScoped<IMailboxRetentionPlanner, MailboxRetentionPlanner>();
    onceBuilder.Services.AddScoped<IMailboxRetentionService, MailboxRetentionService>();
    onceBuilder.Services.AddSingleton<WorkerSingleInstanceLock>();
    onceBuilder.Services.AddSingleton<IWorkerInstanceLock>(sp => sp.GetRequiredService<WorkerSingleInstanceLock>());
    // The loop as a single callable iteration rather than a hosted service.
    onceBuilder.Services.AddSingleton<QueueWorkerService>();
    onceBuilder.Services.AddSingleton<IWorkerPassSource>(sp => sp.GetRequiredService<QueueWorkerService>());
    onceBuilder.Services.AddSingleton<WorkerOnceRunner>();

    using var onceHost = onceBuilder.Build();
    onceHost.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Telemetry").LogTelemetryStatus(onceTelemetry);
    // Environment.ExitCode rather than returning the code: every other path
    // here ends in a bare return, and mixing the two forms is not allowed.
    Environment.ExitCode = await onceHost.Services.GetRequiredService<WorkerOnceRunner>().RunAsync();
    return;
}

if (mode == AppMode.MtaSts)
{
    // The dedicated public policy host: an internet-facing container serving
    // exactly two anonymous routes plus health probes. Deliberately absent:
    // Carter (MapCarter would map the entire console API), static files and the
    // SPA fallback (unmapped paths must 404, not serve the console), the auth
    // middlewares (nothing here is under /api/v1), CORS, startup migration
    // (never migrate from an internet-facing, replica-able pod — the console or
    // a migrate Job owns the schema), hosted services, and credential handling.
    var mtaStsBuilder = WebApplication.CreateBuilder(args);
    ConfigurationPreflight.Validate(mtaStsBuilder.Configuration);
    var mtaStsTelemetry = mtaStsBuilder.AddTelemetry(mode);

    // No localhost fallback, unlike api/worker: this mode exists to face the
    // internet, and a policy host silently reading an empty local database
    // would 404 every client domain while looking healthy.
    var mtaStsConnectionString = ConnectionStringResolver.Resolve(mtaStsBuilder.Configuration)
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Default or DATABASE_URL is required in mta-sts mode.");

    mtaStsBuilder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
        options.UseNpgsql(mtaStsConnectionString));
    mtaStsBuilder.Services.AddMemoryCache();
    mtaStsBuilder.Services.Configure<MtaStsOptions>(mtaStsBuilder.Configuration.GetSection("MtaSts"));
    mtaStsBuilder.Services.AddScoped<IMtaStsPolicyHostService, MtaStsPolicyHostService>();

    var mtaStsNetworkOptions = mtaStsBuilder.Configuration.GetSection("Network").Get<NetworkOptions>() ?? new NetworkOptions();
    var mtaStsForwardedHeaders = new ForwardedHeadersOptions();
    var mtaStsUseForwardedHeaders = ForwardedHeadersSetup.TryConfigure(
        mtaStsNetworkOptions,
        mtaStsForwardedHeaders,
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger(nameof(ForwardedHeadersSetup)));

    var mtaStsApp = mtaStsBuilder.Build();
    mtaStsApp.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Telemetry").LogTelemetryStatus(mtaStsTelemetry);

    if (mtaStsUseForwardedHeaders)
    {
        mtaStsApp.UseForwardedHeaders(mtaStsForwardedHeaders);
    }

    mtaStsApp.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

    // Readiness probes the policies table rather than bare CanConnectAsync:
    // this mode never migrates, so "connects but the schema isn't there yet"
    // must read as not-ready, not as a healthy host that 500s on traffic.
    mtaStsApp.MapGet("/health/ready", async (DmarcAnalyzerDbContext db, CancellationToken ct) =>
    {
        try
        {
            await db.MtaStsPolicies.Select(x => x.Id).FirstOrDefaultAsync(ct);
            return Results.Ok(new { status = "ready" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { status = "unavailable" }, statusCode: 503);
        }
    });

    mtaStsApp.MapMtaStsPublicEndpoints();

    await mtaStsApp.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
ConfigurationPreflight.Validate(builder.Configuration);
var apiTelemetry = builder.AddTelemetry(mode);
var connectionString = ConnectionStringResolver.Resolve(builder.Configuration)
    ?? "Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres";

builder.Services.AddCarter();
// The resolved mode, for anything that reports what this process is — SystemModule.
// Registered rather than re-read from the environment so there is one parse per process
// and one answer; AppRuntimeMode.Parse throws on a bad value, and that has to stay a
// startup crash rather than becoming a 500 on an endpoint.
builder.Services.AddSingleton(new AppRuntimeInfo(mode));
builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddDbContext<DmarcAnalyzerDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddCredentialProtection(builder.Configuration);
builder.Services.AddOidcAuthentication(builder.Configuration);
var passkeyOptions = builder.Configuration.GetSection(PasskeyOptions.SectionName).Get<PasskeyOptions>() ?? new PasskeyOptions();
if (!passkeyOptions.IsValid(builder.Environment.IsDevelopment()))
{
    throw new InvalidOperationException("Auth:Passkeys configuration is invalid. Configure an exact RP ID and matching HTTPS origin.");
}
builder.Services.Configure<PasskeyOptions>(builder.Configuration.GetSection(PasskeyOptions.SectionName));
builder.Services.AddFido2(options =>
{
    options.ServerDomain = passkeyOptions.RelyingPartyId;
    options.ServerName = passkeyOptions.RelyingPartyName;
    options.Origins = passkeyOptions.Origins.ToHashSet(StringComparer.OrdinalIgnoreCase);
    options.ChallengeSize = 32;
});
builder.Services.AddDurableDataProtection(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasskeyCeremonyStore, PasskeyCeremonyStore>();
builder.Services.AddScoped<IPasskeyService, PasskeyService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("passkey-anonymous", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("passkey-management", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Request.Cookies.TryGetValue(SessionCookie.Name, out var sessionCookie)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionCookie)))
            : http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy(ReportUploadModule.RateLimitPolicy, http =>
    {
        var limits = http.RequestServices.GetRequiredService<IOptions<ReportPayloadExtractionOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(
            ReportUploadModule.CredentialRateLimitPartition(http),
            _ => ReportUploadModule.RateLimiterOptions(limits));
    });
});
builder.Services.AddScoped<IOidcSignInService, OidcSignInService>();
builder.Services.AddScoped<CurrentUserContext>();
// Request scopes get the HTTP-backed context; scopes with no request — the startup
// migration, and every worker pass in combined mode — get the system identity, the
// same one worker mode uses. Without this the loop would run under an
// unauthenticated request context that nothing ever populated.
builder.Services.AddScoped<ICurrentUserContext>(sp =>
    sp.GetRequiredService<IHttpContextAccessor>().HttpContext is null
        ? new SystemUserContext()
        : sp.GetRequiredService<CurrentUserContext>());
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IServiceApiCredentialService, ServiceApiCredentialService>();
builder.Services.AddScoped<IServiceApiAuthenticator, ServiceApiAuthenticator>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IDomainService, DomainService>();
builder.Services.AddScoped<IReportSourceService, ReportSourceService>();
builder.Services.AddScoped<IApiSourceCredentialService, ApiSourceCredentialService>();
builder.Services.AddScoped<IApiSourceAuthenticator, ApiSourceAuthenticator>();
builder.Services.AddScoped<IMagicLinkService, MagicLinkService>();
builder.Services.AddScoped<IMagicLinkAuthenticator, MagicLinkAuthenticator>();
builder.Services.AddScoped<IDmarcReportParser, DmarcRuaReportParser>();
builder.Services.AddScoped<ITlsRptReportParser, TlsRptReportParser>();
builder.Services.AddScoped<IDomainIngestResolver, DomainIngestResolver>();
builder.Services.AddScoped<IDmarcReportIngestor, DmarcReportIngestor>();
builder.Services.AddScoped<ITlsReportIngestor, TlsReportIngestor>();
builder.Services.AddSingleton<IPolledSourceTransport, ImapMailboxTransport>();
builder.Services.AddSingleton<IPolledSourceTransport, Pop3MailboxTransport>();
builder.Services.AddSingleton<IPolledSourceTransportFactory, PolledSourceTransportFactory>();
builder.Services.AddSingleton<IPolledSourceTransport, S3ReportSourceTransport>();
builder.Services.AddScoped<IMailboxSyncService, MailboxSyncService>();
builder.Services.AddScoped<ISyncRequestService, SyncRequestService>();
builder.Services.AddScoped<IMailboxSyncRunQueryService, MailboxSyncRunQueryService>();
builder.Services.AddScoped<IMailboxHealthQueryService, MailboxHealthQueryService>();
builder.Services.AddScoped<IAnalyticsQueryService, AnalyticsQueryService>();
builder.Services.AddScoped<IRecordInspectionService, RecordInspectionService>();
builder.Services.AddSpfDriftMonitoring(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddScoped<AuditQueryService>();
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection("Retention"));
builder.Services.AddScoped<IRetentionPurgeService, RetentionPurgeService>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection("Alerts"));
builder.Services.AddScoped<IEmailSender, EmailSender>();
builder.Services.Configure<DigestOptions>(builder.Configuration.GetSection("Digest"));
builder.Services.AddScoped<IAlertEvaluationService, AlertEvaluationService>();
builder.Services.AddScoped<IDigestService, DigestService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IHostnameResolver, HostnameResolver>();
builder.Services.AddSingleton<IAuthoritativeDnsClientLocator, AuthoritativeDnsClientLocator>();
builder.Services.AddSingleton<IDnsTxtResolver, DnsTxtResolver>();
builder.Services.AddScoped<IDmarcPolicyResolver, DmarcPolicyResolver>();
builder.Services.Configure<DnsOptions>(builder.Configuration.GetSection("Dns"));
builder.Services.AddScoped<IDnsPolicyCache, DnsPolicyCache>();
builder.Services.AddMtaStsMonitoring(builder.Configuration);
builder.Services.AddScoped<IMtaStsPolicyHostService, MtaStsPolicyHostService>();
builder.Services.AddScoped<IMtaStsPolicyAdminService, MtaStsPolicyAdminService>();
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.AddOptions<ReportPayloadExtractionOptions>()
    .Bind(builder.Configuration.GetSection(ReportPayloadExtractionOptions.SectionName))
    .Validate(options => options.IsValid(), "Ingestion limits and rate-limit values must be positive; MaxEntryBytes must not exceed MaxExpandedBytes; MaxCompressionRatio must be at least 1")
    .ValidateOnStart();
builder.Services.AddSingleton<IReportPayloadExtractor, BoundedReportPayloadExtractor>();
builder.Services.AddScoped<IReportPayloadIngestor, ReportPayloadIngestor>();
builder.Services.AddScoped<ReportUploadHandler>();
builder.Services.Configure<NetworkOptions>(builder.Configuration.GetSection("Network"));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
builder.Services.AddScoped<IReportMailArchive, ReportMailArchive>();
builder.Services.AddScoped<IBackupExportService, BackupExportService>();
builder.Services.AddScoped<IBackupImportService, BackupImportService>();
builder.Services.AddScoped<IConfigImportPreviewService, ConfigImportPreviewService>();
builder.Services.AddScoped<IBackupOffloadService, BackupOffloadService>();
builder.Services.AddScoped<IMailboxRetentionPlanner, MailboxRetentionPlanner>();
builder.Services.AddScoped<IMailboxRetentionService, MailboxRetentionService>();

if (mode == AppMode.All)
{
    // Everything the loop needs is already registered above — the API host resolves
    // the same sync, alert, digest, retention and DNS services. Combined mode is
    // this one line plus the identity handling below, not a second wiring path.
    builder.Services.AddSingleton<WorkerSingleInstanceLock>();
    builder.Services.AddHostedService<QueueWorkerService>();
}

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("frontend-dev", policy =>
        {
            policy.WithOrigins("http://localhost:5173")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });
}

// Configured before Build so the middleware sees the trust list. Must also run
// before anything that reads Connection.RemoteIpAddress — the audit trail and
// the sign-in paths both do.
var networkOptions = builder.Configuration.GetSection("Network").Get<NetworkOptions>() ?? new NetworkOptions();
var forwardedHeaders = new ForwardedHeadersOptions();
var useForwardedHeaders = ForwardedHeadersSetup.TryConfigure(
    networkOptions,
    forwardedHeaders,
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger(nameof(ForwardedHeadersSetup)));

var app = builder.Build();

app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Telemetry").LogTelemetryStatus(apiTelemetry);

if (useForwardedHeaders)
{
    app.UseForwardedHeaders(forwardedHeaders);
}

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<DmarcAnalyzerDbContext>();

    // Schema changes need room that request-path queries should never have. The
    // AddDmarcReportRecordRangeBegin backfill rewrites 5.3M rows in a single statement
    // — ~94s against Npgsql's 30s default — and a migration that times out midway is a
    // worse outcome than a slow boot. Scoped to this context, which is disposed with
    // the migration, so nothing serving traffic inherits the longer timeout.
    db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

    // A schema change shipped by a deploy is the most audit-worthy event there
    // is, and until now only the manual endpoint recorded one. Capture the
    // pending list first: after MigrateAsync there is nothing left to report,
    // and an empty list means this boot changed nothing and warrants no entry.
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
    await db.Database.MigrateAsync();

    if (pending.Length > 0)
    {
        var audit = migrationScope.ServiceProvider.GetRequiredService<IAuditLog>();
        await audit.RecordSystemAsync(
            AuditEvents.DatabaseMigrated,
            $"Applied {pending.Length} pending database migration{(pending.Length == 1 ? "" : "s")} on startup",
            details: string.Join(", ", pending));
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseCors("frontend-dev");
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

if (app.Configuration.GetValue<bool>("Auth:Oidc:Enabled"))
{
    app.UseAuthentication();
}

app.UseMiddleware<SessionAuthMiddleware>();
app.UseMiddleware<RoleAuthorizationMiddleware>();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (DmarcAnalyzerDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "unavailable" }, statusCode: 503));

// Explicit routes win over MapFallbackToFile, so without this the well-known
// path would answer with the SPA — 200, text/html, and silently wrong.
app.MapMtaStsPublicEndpoints();

app.MapCarter();
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program;
