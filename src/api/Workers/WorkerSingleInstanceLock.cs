using DmarcAnalyzer.Api.Data;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DmarcAnalyzer.Api.Workers;

/// <summary>
/// Waits for exclusive ingestion ownership without blocking API startup.
/// <para>
/// Two ingestion loops against one database is not a supported configuration, and
/// the failures are quiet ones: two IMAP sessions per mailbox, two
/// <c>mailbox_sync_run</c> rows inflating the health counts, duplicate alert
/// emails (the cooldown is a read-then-write with no unique constraint behind
/// it), a duplicate monthly digest sent before the unique index rejects the
/// second row, <c>DbUpdateConcurrencyException</c> from the retention purge
/// deleting a batch another worker already deleted, and a checkpoint that can
/// move backwards because the update is unconditional. Reports themselves survive
/// — every insert is <c>ON CONFLICT DO NOTHING</c> against a real unique index —
/// so nothing corrupts. It just does the work twice and tells the operator things
/// that are not true.
/// </para>
/// <para>
/// The Helm chart refuses <c>worker.replicas &gt; 1</c>, but that only covers
/// Kubernetes: <c>docker compose up --scale worker=2</c> is not prevented by
/// anything Compose can express, and neither is running a worker beside an
/// <c>APP_MODE=all</c> container. A lock in the database covers every way of
/// arriving at the same state.
/// </para>
/// </summary>
public sealed class WorkerSingleInstanceLock(
    IConfiguration configuration,
    IOptions<WorkerOptions> options,
    ILogger<WorkerSingleInstanceLock> logger) : IWorkerInstanceLock, IAsyncDisposable
{
    /// <summary>
    /// Arbitrary but fixed: any two processes using this key contend, and nothing
    /// else in the database will pick it by accident.
    /// </summary>
    private const long LockKey = 0x444D_4152_4357_4B52; // "DMARCWKR"

    private NpgsqlConnection? _connection;

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnforceSingleInstance)
        {
            logger.LogWarning(
                "Worker:EnforceSingleInstance is off. Nothing prevents a second worker from " +
                "running against this database, which duplicates ingestion and can send " +
                "duplicate alert and digest email.");
            return;
        }

        var connectionString = ConnectionStringResolver.Resolve(configuration);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Nothing to lock against, and the loop is about to fail on the same
            // missing setting with a clearer message than this one would give.
            return;
        }

        // A dedicated connection, held open for the life of the process: advisory
        // locks are scoped to a session, so it has to be this connection rather
        // than one borrowed from the pool and returned.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
            }.ConnectionString);
            try
            {
                await _connection.OpenAsync(cancellationToken);
                await using var command = _connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_lock(@key)";
                command.CommandTimeout = 0;
                command.Parameters.AddWithValue("key", LockKey);
                logger.LogInformation("Waiting for exclusive ingestion ownership; API remains available.");
                await command.ExecuteNonQueryAsync(cancellationToken);
                break;
            }
            catch (NpgsqlException ex) when (ex.IsTransient && !cancellationToken.IsCancellationRequested)
            {
                // A sidecar forward may start after the API. No ingestion pass
                // runs before the lock, and failed sessions close before retry.
                await DisposeAsync();
                logger.LogWarning("Database unavailable while waiting for ingestion ownership; retrying in five seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch
            {
                await DisposeAsync();
                throw;
            }
        }

        logger.LogInformation("Acquired the ingestion lock; this is the only worker on this database.");
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnforceSingleInstance)
        {
            LogEnforcementOffOnce();
            return true;
        }

        var connectionString = ConnectionStringResolver.Resolve(configuration);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Same reasoning as above: the passes are about to fail on the
            // same missing setting with a clearer message.
            return true;
        }

        // A dedicated non-pooled connection, like the blocking path: the lock
        // lives on the session, so the connection that took it must stay open.
        // It only becomes this instance's connection on success; a failed try
        // closes its own, so a contended caller holds nothing at all.
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            command.Parameters.AddWithValue("key", LockKey);
            var acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;

            if (acquired)
            {
                _connection = connection;
                logger.LogInformation("Acquired the ingestion lock; this is the only worker on this database.");
                return true;
            }

            await connection.DisposeAsync();
            return false;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private bool _enforcementOffWarningLogged;

    private void LogEnforcementOffOnce()
    {
        // Try-acquire is polled, so the warning AcquireAsync logs inline would
        // repeat every second for the whole lock wait. Once is enough.
        if (_enforcementOffWarningLogged)
        {
            return;
        }

        _enforcementOffWarningLogged = true;
        logger.LogWarning(
            "Worker:EnforceSingleInstance is off. Nothing prevents a second worker from " +
            "running against this database, which duplicates ingestion and can send " +
            "duplicate alert and digest email.");
    }

    /// <summary>
    /// Closing the connection releases the lock. Postgres also releases it if this
    /// process dies without closing, once it notices the connection is gone.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
