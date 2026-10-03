using DmarcAnalyzer.Api.Application.Common;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// Durable manual sync: the console enqueues a row on <c>sync_request</c> and
/// polls it, while a worker claims queued rows and writes outcomes back.
/// <para>
/// The API half (<see cref="EnqueueAsync"/>, <see cref="GetAsync"/>) is
/// tenant-scoped like every other read: cross-tenant ids answer 404. The
/// worker half (<see cref="ClaimNextAsync"/> and the finish methods) runs as
/// the system identity across every tenant by design, like the passes
/// themselves.
/// </para>
/// </summary>
public interface ISyncRequestService
{
    /// <summary>
    /// Queues a sync for one source. When the source already has a queued or
    /// running request, that request is returned with <c>IsNew</c> false
    /// instead of queueing a second one — the dedupe is race-safe, so two
    /// callers racing each other still produce one row.
    /// </summary>
    Task<ServiceResult<SyncRequestEnqueueResult>> EnqueueAsync(Guid reportSourceId, CancellationToken ct);

    /// <summary>One request by id, or 404 when it is unknown or cross-tenant.</summary>
    Task<ServiceResult<SyncRequestDetails>> GetAsync(Guid requestId, CancellationToken ct);

    /// <summary>
    /// Atomically claims the oldest queued request for the calling worker:
    /// exactly one claimant wins each row. Null when nothing is queued.
    /// </summary>
    Task<SyncRequestClaim?> ClaimNextAsync(CancellationToken ct);

    /// <summary>
    /// Marks a request completed with the sync's outcome payload. False when
    /// the row is unknown or already terminal.
    /// </summary>
    Task<bool> CompleteAsync(Guid requestId, string? resultJson, CancellationToken ct);

    /// <summary>
    /// Marks a request partial: the run stopped early but kept its checkpoint,
    /// so the next pass resumes where this one stopped. False when the row is
    /// unknown or already terminal.
    /// </summary>
    Task<bool> MarkPartialAsync(Guid requestId, string? resultJson, CancellationToken ct);

    /// <summary>
    /// Marks a request failed with the reason. False when the row is unknown
    /// or already terminal.
    /// </summary>
    Task<bool> FailAsync(Guid requestId, string error, string? resultJson, CancellationToken ct);

    /// <summary>
    /// Writes progress onto a running request without finishing it. False when
    /// the row is unknown or not running.
    /// </summary>
    Task<bool> HeartbeatAsync(Guid requestId, string? progressJson, CancellationToken ct);

    /// <summary>
    /// Returns abandoned <c>running</c> rows to <c>queued</c>: a worker that
    /// dies mid-sync never finishes its claim, so without this the request
    /// (and the source's one-live-row slot) wedges forever. Rows interrupted
    /// <see cref="SyncRequestService.MaxInterruptedAttempts"/> times fail
    /// instead — something is killing the worker on that source. Returns the
    /// number of rows moved.
    /// </summary>
    Task<int> RequeueStaleAsync(TimeSpan staleAfter, CancellationToken ct);
}
