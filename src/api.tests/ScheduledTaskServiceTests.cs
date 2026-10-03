using DmarcAnalyzer.Api.Application.Maintenance;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ScheduledTaskServiceTests
{
    private static DmarcAnalyzerDbContext NewDb(string? name = null)
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options);

    [Fact]
    public async Task UnknownTaskIsDue()
    {
        await using var db = NewDb();
        var service = new ScheduledTaskService(db);

        Assert.True(await service.IsDueAsync("alert", TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task IsDueDoesNotCreateRow()
    {
        await using var db = NewDb();
        var service = new ScheduledTaskService(db);

        Assert.True(await service.IsDueAsync("alert", TimeSpan.FromHours(1)));
        Assert.Null(await db.ScheduledTaskStates.FindAsync(["alert"]));
    }

    [Fact]
    public async Task RecordedRunIsNotDueWithinInterval()
    {
        await using var db = NewDb();
        var service = new ScheduledTaskService(db);

        await service.RecordRunAsync("alert", success: true);

        Assert.False(await service.IsDueAsync("alert", TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task TaskIsDueAgainOnceIntervalHasElapsed()
    {
        await using var db = NewDb();
        db.ScheduledTaskStates.Add(new ScheduledTaskState
        {
            TaskKey = "dns_refresh",
            LastRunAtUtc = DateTime.UtcNow.AddHours(-7),
            LastSuccessAtUtc = DateTime.UtcNow.AddHours(-7),
        });
        await db.SaveChangesAsync();
        var service = new ScheduledTaskService(db);

        Assert.True(await service.IsDueAsync("dns_refresh", TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task TaskIsNotDueBeforeIntervalHasElapsed()
    {
        await using var db = NewDb();
        db.ScheduledTaskStates.Add(new ScheduledTaskState
        {
            TaskKey = "dns_refresh",
            LastRunAtUtc = DateTime.UtcNow.AddHours(-5),
            LastSuccessAtUtc = DateTime.UtcNow.AddHours(-5),
        });
        await db.SaveChangesAsync();
        var service = new ScheduledTaskService(db);

        Assert.False(await service.IsDueAsync("dns_refresh", TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task RecordRunLazyCreatesRow()
    {
        await using var db = NewDb();
        var service = new ScheduledTaskService(db);
        var before = DateTime.UtcNow;

        await service.RecordRunAsync("retention", success: true);

        var row = await db.ScheduledTaskStates.FindAsync(["retention"]);
        Assert.NotNull(row);
        Assert.NotNull(row.LastRunAtUtc);
        Assert.NotNull(row.LastSuccessAtUtc);
        Assert.True(row.LastRunAtUtc >= before);
        Assert.Equal(0, row.ConsecutiveFailures);
    }

    [Fact]
    public async Task SuccessfulRunResetsConsecutiveFailures()
    {
        await using var db = NewDb();
        db.ScheduledTaskStates.Add(new ScheduledTaskState
        {
            TaskKey = "mta_sts",
            LastRunAtUtc = DateTime.UtcNow.AddHours(-7),
            ConsecutiveFailures = 3,
        });
        await db.SaveChangesAsync();
        var service = new ScheduledTaskService(db);

        await service.RecordRunAsync("mta_sts", success: true);

        var row = await db.ScheduledTaskStates.FindAsync(["mta_sts"]);
        Assert.NotNull(row);
        Assert.Equal(0, row.ConsecutiveFailures);
        Assert.NotNull(row.LastSuccessAtUtc);
        Assert.False(await service.IsDueAsync("mta_sts", TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task FailedRunAdvancesClockAndCountsFailure()
    {
        await using var db = NewDb();
        var service = new ScheduledTaskService(db);

        await service.RecordRunAsync("spf_drift", success: false);
        await service.RecordRunAsync("spf_drift", success: false);

        var row = await db.ScheduledTaskStates.FindAsync(["spf_drift"]);
        Assert.NotNull(row);
        Assert.Equal(2, row.ConsecutiveFailures);
        Assert.NotNull(row.LastRunAtUtc);
        Assert.Null(row.LastSuccessAtUtc);
        // A recorded failure still waits out the interval, like the old
        // in-memory timestamp the swallowing passes always advanced.
        Assert.False(await service.IsDueAsync("spf_drift", TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task TaskKeysAreIndependent()
    {
        await using var db = NewDb();
        var service = new ScheduledTaskService(db);

        await service.RecordRunAsync("alert", success: true);

        Assert.False(await service.IsDueAsync("alert", TimeSpan.FromHours(1)));
        Assert.True(await service.IsDueAsync("digest", TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task RecordedRunIsVisibleToFreshContext()
    {
        // The restart case: a run recorded through one context must read as
        // not-due through a new process-equivalent context on the same store,
        // so a restart never re-runs a fresh task.
        var name = Guid.NewGuid().ToString("N");
        await using (var db = NewDb(name))
        {
            await new ScheduledTaskService(db).RecordRunAsync("backup_offload", success: true);
        }

        await using var fresh = NewDb(name);
        Assert.False(await new ScheduledTaskService(fresh)
            .IsDueAsync("backup_offload", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void WorkerTaskKeysMatchContract()
    {
        Assert.Equal("alert", ScheduledTaskKeys.Alert);
        Assert.Equal("digest", ScheduledTaskKeys.Digest);
        Assert.Equal("retention", ScheduledTaskKeys.Retention);
        Assert.Equal("dns_refresh", ScheduledTaskKeys.DnsRefresh);
        Assert.Equal("mta_sts", ScheduledTaskKeys.MtaSts);
        Assert.Equal("spf_drift", ScheduledTaskKeys.SpfDrift);
        Assert.Equal("backup_offload", ScheduledTaskKeys.BackupOffload);
        Assert.Equal("mailbox_retention", ScheduledTaskKeys.MailboxRetention);
    }
}
