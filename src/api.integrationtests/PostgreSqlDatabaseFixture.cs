using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// Owns one randomly named PostgreSQL database for the lifetime of an xUnit
/// collection. Reusing this fixture type from another collection creates a
/// separate database, so suites can share the harness without sharing state.
/// </summary>
public sealed class PostgreSqlDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionStringEnvironmentVariable = "DMARC_TEST_POSTGRES";

    private readonly string _adminConnectionString;
    private readonly string _databaseName = $"dmarc_it_{Guid.NewGuid():N}";

    public PostgreSqlDatabaseFixture()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringEnvironmentVariable} must point to an administrative PostgreSQL database.");
        }

        var admin = new NpgsqlConnectionStringBuilder(configured);
        if (string.IsNullOrWhiteSpace(admin.Database))
        {
            admin.Database = "postgres";
        }

        _adminConnectionString = admin.ConnectionString;
        admin.Database = _databaseName;
        admin.ApplicationName = "dmarc-analyzer-integration-tests";
        ConnectionString = admin.ConnectionString;
    }

    public string ConnectionString { get; }

    public DmarcAnalyzerDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
        await command.ExecuteNonQueryAsync();
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        await command.ExecuteNonQueryAsync();
    }

    public async Task MigrateToLatestAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
