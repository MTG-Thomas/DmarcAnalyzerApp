using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;

namespace DmarcAnalyzer.Api.Application.Maintenance;

/// <summary>
/// The durable maintenance cadence behind <see cref="IScheduledTaskService"/>.
/// Backed by <c>scheduled_task_state</c>; rows are lazy-created on first
/// record so new tasks need no seeding and no schema change.
/// </summary>
public sealed class ScheduledTaskService(DmarcAnalyzerDbContext db) : IScheduledTaskService
{
    /// <inheritdoc />
    public async Task<bool> IsDueAsync(string taskKey, TimeSpan interval, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        var state = await db.ScheduledTaskStates.FindAsync([taskKey], ct);
        if (state?.LastRunAtUtc is not { } last)
        {
            return true;
        }

        return DateTime.UtcNow - last >= interval;
    }

    /// <inheritdoc />
    public async Task RecordRunAsync(string taskKey, bool success, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        var now = DateTime.UtcNow;
        var state = await db.ScheduledTaskStates.FindAsync([taskKey], ct);
        if (state is null)
        {
            state = new ScheduledTaskState { TaskKey = taskKey };
            db.ScheduledTaskStates.Add(state);
        }

        state.LastRunAtUtc = now;
        state.UpdatedAtUtc = now;
        if (success)
        {
            state.LastSuccessAtUtc = now;
            state.ConsecutiveFailures = 0;
        }
        else
        {
            state.ConsecutiveFailures++;
        }

        await db.SaveChangesAsync(ct);
    }
}
