using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
public sealed class WorkerHandoverIntegrationTests(PostgreSqlDatabaseFixture database)
{
    private WorkerSingleInstanceLock CreateLock(string name)
    {
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = name,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection.ConnectionString }).Build();
        return new WorkerSingleInstanceLock(configuration, Options.Create(new WorkerOptions()),
            NullLogger<WorkerSingleInstanceLock>.Instance);
    }

    private async Task WaitForStandbyAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name AND wait_event = 'advisory'";
        query.Parameters.AddWithValue("name", name);
        while ((long)(await query.ExecuteScalarAsync(timeout.Token))! != 1)
            await Task.Delay(20, timeout.Token);
    }

    [Fact]
    public async Task BackgroundWorkerStartsButDoesNotRunPassUntilPreviousOwnerExits()
    {
        await using var previous = CreateLock("handover-previous");
        await previous.AcquireAsync(default);
        await using var replacement = CreateLock("handover-replacement");
        var scopes = new RecordingScopes();
        using var worker = new QueueWorkerService(scopes, replacement, Options.Create(new WorkerOptions()),
            Options.Create(new BackupOptions()), NullLogger<QueueWorkerService>.Instance);
        try
        {
            await worker.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForStandbyAsync("handover-replacement");
            Assert.False(scopes.Entered.Task.IsCompleted);
            await previous.DisposeAsync();
            await scopes.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StoppingStandbyCancelsWaitWithoutRunningPassOrRetainingLock()
    {
        await using var previous = CreateLock("cancel-previous");
        await previous.AcquireAsync(default);
        await using var replacement = CreateLock("cancel-replacement");
        var scopes = new RecordingScopes();
        using var worker = new QueueWorkerService(scopes, replacement, Options.Create(new WorkerOptions()),
            Options.Create(new BackupOptions()), NullLogger<QueueWorkerService>.Instance);
        await worker.StartAsync(default);
        await WaitForStandbyAsync("cancel-replacement");
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(scopes.Entered.Task.IsCompleted);
        await previous.DisposeAsync();
        await using var next = CreateLock("cancel-next");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await next.AcquireAsync(timeout.Token);
    }

    private sealed class RecordingScopes : IServiceScopeFactory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IServiceScope CreateScope()
        {
            Entered.TrySetResult();
            throw new InvalidOperationException("Test records entry; no ingestion services are installed.");
        }
    }
}
