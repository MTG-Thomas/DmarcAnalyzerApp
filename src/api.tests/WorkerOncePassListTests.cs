using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The run-once path executes <see cref="QueueWorkerService"/>'s pass list, so
/// that list must be exactly the loop body — every step, in loop order. A pass
/// dropped here would run in the loop forever and never in a scheduled slot.
/// </summary>
public sealed class WorkerOncePassListTests
{
    [Fact]
    public void PassListCoversEveryLoopStepInOrder()
    {
        var worker = new QueueWorkerService(
            NoScopes.Instance,
            new WorkerSingleInstanceLock(
                new ConfigurationBuilder().Build(),
                Options.Create(new WorkerOptions()),
                NullLogger<WorkerSingleInstanceLock>.Instance),
            Options.Create(new WorkerOptions()),
            Options.Create(new BackupOptions()),
            NullLogger<QueueWorkerService>.Instance);

        var names = worker.GetPasses().Select(p => p.Name).ToArray();

        Assert.Equal(
        [
            "stale-sync-close",
            "sync-request-drain",
            "scheduled-sync",
            "alerts",
            "digest",
            "retention",
            "dns-refresh",
            "mta-sts",
            "spf-drift",
            "backup-offload",
            "mailbox-retention",
        ], names);
    }

    private sealed class NoScopes : IServiceScopeFactory
    {
        public static readonly NoScopes Instance = new();

        public IServiceScope CreateScope()
            => throw new InvalidOperationException("Listing passes must not need a scope.");
    }
}
