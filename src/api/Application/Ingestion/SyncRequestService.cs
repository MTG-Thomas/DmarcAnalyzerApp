using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>EF-backed <see cref="ISyncRequestService"/>.</summary>
public sealed class SyncRequestService(
    DmarcAnalyzerDbContext db,
    IPolledSourceTransportFactory transports,
    ICurrentUserContext currentUser) : ISyncRequestService
{
    /// <inheritdoc />
    public async Task<ServiceResult<SyncRequestEnqueueResult>> EnqueueAsync(Guid reportSourceId, CancellationToken ct)
    {
        var source = await db.ReportSources
            .SingleOrDefaultAsync(x => x.Id == reportSourceId, ct);
        if (source is null || !currentUser.CanAccessClient(source.DefaultClientId))
        {
            return ServiceResult<SyncRequestEnqueueResult>.Failure("report source not found", 404);
        }

        // Resolved rather than tested against a list of protocol names, exactly
        // as the sync itself does: a source is syncable exactly when a transport
        // exists for it, so the two can never disagree.
        if (transports.For(source.Protocol) is null)
        {
            return ServiceResult<SyncRequestEnqueueResult>.Failure(
                $"sync applies to polled mailboxes only; this source's protocol is '{source.Protocol}'", 400);
        }

        if (ReportSourceProtocols.IsMailbox(source.Protocol) &&
            (string.IsNullOrWhiteSpace(source.Host)
            || source.Port is not > 0
            || !source.UseTls.HasValue
            || string.IsNullOrWhiteSpace(source.Username)
            || string.IsNullOrWhiteSpace(source.PasswordEncrypted)))
        {
            return ServiceResult<SyncRequestEnqueueResult>.Failure("mailbox source configuration is incomplete", 409);
        }

        var live = await FindLiveAsync(reportSourceId, ct);
        if (live is not null)
        {
            return ServiceResult<SyncRequestEnqueueResult>.Success(
                new SyncRequestEnqueueResult(live.Id, live.Status, IsNew: false));
        }

        // A service caller has no user row — its UserId is the credential id —
        // so only a signed-in user is recorded as the requester.
        var request = new SyncRequest
        {
            ReportSourceId = source.Id,
            Status = SyncRequestStatus.Queued,
            RequestedByUserId = currentUser is { IsAuthenticated: true, ActorType: "user" } &&
                currentUser.UserId != Guid.Empty
                ? currentUser.UserId
                : null,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.SyncRequests.Add(request);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the insert race: another enqueue landed first and the
            // partial unique index refused this one. Answer with the winner
            // rather than an error. When no live row exists the failure was
            // something else, so it is rethrown, not swallowed.
            db.ChangeTracker.Clear();
            var winner = await FindLiveAsync(reportSourceId, ct);
            if (winner is null)
            {
                throw;
            }

            return ServiceResult<SyncRequestEnqueueResult>.Success(
                new SyncRequestEnqueueResult(winner.Id, winner.Status, IsNew: false));
        }

        return ServiceResult<SyncRequestEnqueueResult>.Success(
            new SyncRequestEnqueueResult(request.Id, SyncRequestStatus.Queued, IsNew: true));
    }

    /// <inheritdoc />
    public async Task<ServiceResult<SyncRequestDetails>> GetAsync(Guid requestId, CancellationToken ct)
    {
        var request = await db.SyncRequests
            .AsNoTracking()
            .Include(x => x.ReportSource)
            .SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (request?.ReportSource is null ||
            !currentUser.CanAccessClient(request.ReportSource.DefaultClientId))
        {
            return ServiceResult<SyncRequestDetails>.Failure("sync request not found", 404);
        }

        return ServiceResult<SyncRequestDetails>.Success(ToDetails(request));
    }

    /// <inheritdoc />
    public async Task<SyncRequestClaim?> ClaimNextAsync(CancellationToken ct)
    {
        // The production claim is one conditional UPDATE per pass: the losers
        // of a race see zero rows and look again, so exactly one claimant
        // wins each row. The InMemory provider cannot execute that bulk
        // update, so tests take the tracked-entity path below — same outcome,
        // single-threaded callers only.
        if (!db.Database.IsRelational())
        {
            var pending = await db.SyncRequests
                .Where(x => x.Status == SyncRequestStatus.Queued)
                .OrderBy(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (pending is null)
            {
                return null;
            }

            pending.Status = SyncRequestStatus.Running;
            pending.StartedAtUtc = DateTime.UtcNow;
            pending.Attempts++;
            await db.SaveChangesAsync(ct);
            return new SyncRequestClaim(pending.Id, pending.ReportSourceId, pending.Attempts);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = await db.SyncRequests
                .AsNoTracking()
                .Where(x => x.Status == SyncRequestStatus.Queued)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.Id)
                .FirstOrDefaultAsync(ct);
            if (id == Guid.Empty)
            {
                return null;
            }

            var startedAtUtc = DateTime.UtcNow;
            var claimed = await db.SyncRequests
                .Where(x => x.Id == id && x.Status == SyncRequestStatus.Queued)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, SyncRequestStatus.Running)
                    .SetProperty(x => x.StartedAtUtc, startedAtUtc)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (claimed == 0)
            {
                continue;
            }

            // The bulk update bypassed the tracker, so a copy this scope is
            // already holding (the enqueue just above did) still says queued.
            // Evict it: every later read in this scope must see the claim.
            var tracked = db.ChangeTracker.Entries<SyncRequest>()
                .SingleOrDefault(entry => entry.Entity.Id == id);
            if (tracked is not null)
            {
                tracked.State = EntityState.Detached;
            }

            var row = await db.SyncRequests
                .AsNoTracking()
                .SingleAsync(x => x.Id == id, ct);
            return new SyncRequestClaim(row.Id, row.ReportSourceId, row.Attempts);
        }

        return null;
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(Guid requestId, string? resultJson, CancellationToken ct)
        => FinishAsync(requestId, SyncRequestStatus.Completed, resultJson, error: null, ct);

    /// <inheritdoc />
    public Task<bool> MarkPartialAsync(Guid requestId, string? resultJson, CancellationToken ct)
        => FinishAsync(requestId, SyncRequestStatus.Partial, resultJson, error: null, ct);

    /// <inheritdoc />
    public Task<bool> FailAsync(Guid requestId, string error, string? resultJson, CancellationToken ct)
        => FinishAsync(requestId, SyncRequestStatus.Failed, resultJson, error, ct);

    /// <inheritdoc />
    public async Task<bool> HeartbeatAsync(Guid requestId, string? progressJson, CancellationToken ct)
    {
        var request = await db.SyncRequests.SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (request is null || request.Status != SyncRequestStatus.Running)
        {
            return false;
        }

        if (progressJson is not null)
        {
            request.ResultJson = Truncate(progressJson, 8000);
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    private async Task<bool> FinishAsync(
        Guid requestId,
        string status,
        string? resultJson,
        string? error,
        CancellationToken ct)
    {
        var request = await db.SyncRequests.SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (request is null || IsTerminal(request.Status))
        {
            return false;
        }

        request.Status = status;
        request.FinishedAtUtc = DateTime.UtcNow;
        if (resultJson is not null)
        {
            request.ResultJson = Truncate(resultJson, 8000);
        }

        if (error is not null)
        {
            request.LastError = Truncate(error, 2000);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool IsTerminal(string status)
        => status is SyncRequestStatus.Completed
            or SyncRequestStatus.Partial
            or SyncRequestStatus.Failed
            or SyncRequestStatus.Cancelled;

    private Task<SyncRequest?> FindLiveAsync(Guid reportSourceId, CancellationToken ct)
        => db.SyncRequests
            .AsNoTracking()
            .Where(x => x.ReportSourceId == reportSourceId &&
                (x.Status == SyncRequestStatus.Queued || x.Status == SyncRequestStatus.Running))
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    private static SyncRequestDetails ToDetails(SyncRequest request)
        => new(
            request.Id,
            request.ReportSourceId,
            request.Status,
            request.CreatedAtUtc,
            request.StartedAtUtc,
            request.FinishedAtUtc,
            request.Attempts,
            request.LastError,
            request.ResultJson);

    /// <summary>
    /// The entity promises the writers truncate; this is the writer. The
    /// columns refuse overlong values at the insert, so an untruncated write
    /// would fail the finish rather than merely overflowing the display.
    /// </summary>
    private static string Truncate(string value, int maxLength)
        => value.Length > maxLength ? value[..maxLength] : value;
}
