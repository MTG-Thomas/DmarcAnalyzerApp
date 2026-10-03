using System.Text.Json;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// The <c>sync_request.Status</c> values, named once. The table carries a check
/// constraint on the same set, so a value added here needs a migration too.
/// </summary>
public static class SyncRequestStatus
{
    /// <summary>Enqueued, waiting for a worker to claim it.</summary>
    public const string Queued = "queued";

    /// <summary>Claimed by a worker that is syncing the source now.</summary>
    public const string Running = "running";

    /// <summary>The sync finished and every message was drained.</summary>
    public const string Completed = "completed";

    /// <summary>The sync stopped early (timeout or budget) with the checkpoint held.</summary>
    public const string Partial = "partial";

    /// <summary>The sync failed; <see cref="SyncRequestDetails.Error"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>Withdrawn before or during the run; the worker must not act on it.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// What an enqueue returns: the request's identity and liveness, plus whether
/// this call created it (202) or found one already in flight (200).
/// </summary>
public sealed record SyncRequestEnqueueResult(
    Guid RequestId,
    string Status,
    bool IsNew);

/// <summary>
/// The POST /sync response body: the request plus where to poll it.
/// </summary>
public sealed record SyncRequestEnqueueResponse(
    Guid RequestId,
    string Status,
    string StatusUrl);

/// <summary>
/// One sync request as the status endpoint returns it. <c>Summary</c> is the
/// outcome payload JSON the worker wrote (or its latest progress write);
/// <c>Error</c> is why the last attempt failed.
/// </summary>
public sealed record SyncRequestDetails(
    Guid RequestId,
    Guid ReportSourceId,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    int Attempts,
    string? Error,
    JsonElement? Summary);

/// <summary>
/// What a worker claim hands back: enough to sync the source and finish the row.
/// </summary>
public sealed record SyncRequestClaim(
    Guid RequestId,
    Guid ReportSourceId,
    int Attempts);
