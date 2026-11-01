using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DmarcAnalyzer.Api.Application.Observability;

/// <summary>One identity SELECT through the application's normal Npgsql pool.</summary>
public sealed class DatabaseReadinessProbe(
    DatabaseSessionIdentityObserver observer,
    ILogger<DatabaseReadinessProbe> logger)
{
    public const string IdentityQuery = "SELECT session_user, current_user, pg_catalog.current_database()";

    public async Task<bool> CheckAsync(DmarcAnalyzerDbContext db, CancellationToken ct)
    {
        try
        {
            // Same application DbContext/data source, not a diagnostic connection.
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                var key = (connection.Host, connection.Port, connection.ProcessID);
                var generation = observer.CaptureGeneration(key);
                await using var command = connection.CreateCommand();
                command.CommandText = IdentityQuery;
                command.CommandTimeout = 2;
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || reader.FieldCount != 3) return false;
                observer.Observe(key, generation, reader.GetString(0), reader.GetString(1), reader.GetString(2));
                return true;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Never emit an exception/connection string/provider error body.
            logger.LogWarning("Database readiness check failed: error_type={ErrorType}", error.GetType().Name);
            return false;
        }
    }
}
