using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class ServerlessSchemaTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task MigrationApplies_CreatesServerlessDurabilityTables()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        await using var db = database.CreateDbContext();
        foreach (var table in new[] { "scheduled_task_state", "sync_request", "passkey_ceremony" })
        {
            Assert.True(await db.Database.SqlQueryRaw<bool>(
                $"SELECT to_regclass('public.{table}') IS NOT NULL AS \"Value\"").SingleAsync());
        }

        // The one-live-request-per-source guard must exist and be partial.
        var partialIndexPredicate = await db.Database.SqlQueryRaw<string>(
            """
            SELECT pg_get_expr(indpred, indrelid) AS "Value"
            FROM pg_index
            WHERE indexrelid = '"IX_sync_request_ReportSourceId"'::regclass
            """).SingleAsync();
        Assert.Contains("queued", partialIndexPredicate, StringComparison.Ordinal);
        Assert.Contains("running", partialIndexPredicate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncRequest_PartialUnique_RejectsSecondQueuedRow()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();
        var sourceId = await SeedSourceAsync();

        await using (var db = database.CreateDbContext())
        {
            db.SyncRequests.Add(new SyncRequest { ReportSourceId = sourceId, Status = "queued" });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
        {
            db.SyncRequests.Add(new SyncRequest { ReportSourceId = sourceId, Status = "queued" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = database.CreateDbContext())
        {
            db.SyncRequests.Add(new SyncRequest { ReportSourceId = sourceId, Status = "running" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Terminal rows are outside the predicate: completing the first row
        // frees the source for a new request, and history accumulates.
        await using (var db = database.CreateDbContext())
        {
            var first = await db.SyncRequests.SingleAsync();
            first.Status = "completed";
            first.StartedAtUtc = DateTime.UtcNow;
            first.FinishedAtUtc = DateTime.UtcNow;
            db.SyncRequests.Add(new SyncRequest { ReportSourceId = sourceId, Status = "queued" });
            await db.SaveChangesAsync();
        }

        await using (var verification = database.CreateDbContext())
        {
            Assert.Equal(2, await verification.SyncRequests.CountAsync());
        }
    }

    [Fact]
    public async Task PasskeyCeremony_AtomicConsume_TwoConnectionsOneWins()
    {
        await database.ResetDatabaseAsync();
        await database.MigrateToLatestAsync();

        const string handle = "ceremony-race-handle";
        await using (var db = database.CreateDbContext())
        {
            db.PasskeyCeremonyStates.Add(new PasskeyCeremonyState
            {
                Handle = handle,
                Challenge = [1, 2, 3, 4],
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            });
            await db.SaveChangesAsync();
        }

        // Two hosts completing the same ceremony at once: the atomic consume
        // (UPDATE ... WHERE "ConsumedAtUtc" IS NULL) lets exactly one win.
        static async Task<int> TryConsumeAsync(string connectionString, string ceremonyHandle)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """UPDATE passkey_ceremony SET "ConsumedAtUtc" = CURRENT_TIMESTAMP WHERE "Handle" = $1 AND "ConsumedAtUtc" IS NULL""";
            command.Parameters.AddWithValue(ceremonyHandle);
            return await command.ExecuteNonQueryAsync();
        }

        var attempts = await Task.WhenAll(
            TryConsumeAsync(database.ConnectionString, handle),
            TryConsumeAsync(database.ConnectionString, handle));
        Assert.Equal(1, attempts.Sum());

        await using var verification = database.CreateDbContext();
        Assert.NotNull((await verification.PasskeyCeremonyStates.SingleAsync()).ConsumedAtUtc);
    }

    private async Task<Guid> SeedSourceAsync()
    {
        var client = new Client
        {
            Name = "Serverless schema fixture",
            Slug = $"serverless-schema-{Guid.NewGuid():N}",
            Timezone = "UTC",
        };
        var source = new ReportSource
        {
            Name = "Serverless schema source",
            Protocol = "api",
            UseTls = null,
            DefaultClientId = client.Id,
        };

        await using var db = database.CreateDbContext();
        db.AddRange(client, source);
        await db.SaveChangesAsync();

        return source.Id;
    }
}
