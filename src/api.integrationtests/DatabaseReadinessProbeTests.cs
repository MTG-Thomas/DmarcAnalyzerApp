using DmarcAnalyzer.Api.Application.Observability;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

[Collection(PostgreSqlCollections.Persistence)]
[Trait("Category", "Persistence")]
public sealed class DatabaseReadinessProbeTests(PostgreSqlDatabaseFixture database)
{
    [Fact]
    public async Task ReportsOnlyAfterReusingARealPhysicalConnectionAndNotItsReplacement()
    {
        var logger = new RecordingLogger();
        var observer = new DatabaseSessionIdentityObserver(logger);
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            MaxPoolSize = 1,
            ApplicationName = $"runtime-identity-test-{Guid.NewGuid():N}"
        };
        var opened = 0;
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
        void Register(NpgsqlConnection connection)
        {
            opened++;
            observer.PhysicalConnectionOpened((connection.Host, connection.Port, connection.ProcessID));
        }
        builder.UsePhysicalConnectionInitializer(Register, connection =>
        {
            Register(connection);
            return Task.CompletedTask;
        });
        await using var source = builder.Build();
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>().UseNpgsql(source).Options;
        var probe = new DatabaseReadinessProbe(observer, NullLogger<DatabaseReadinessProbe>.Instance);
        await using (var db = new DmarcAnalyzerDbContext(options))
            Assert.True(await probe.CheckAsync(db, CancellationToken.None));
        Assert.Empty(logger.Messages);
        // Only this test's private data source is cleared; no shared application pool.
        source.Clear();
        await using (var db = new DmarcAnalyzerDbContext(options))
            Assert.True(await probe.CheckAsync(db, CancellationToken.None));
        Assert.Empty(logger.Messages);
        await using (var db = new DmarcAnalyzerDbContext(options))
            Assert.True(await probe.CheckAsync(db, CancellationToken.None));
        Assert.Equal(2, opened);
        Assert.Contains("physical_connection_previously_observed=true", Assert.Single(logger.Messages));
    }

    private sealed class RecordingLogger : ILogger<DatabaseSessionIdentityObserver>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
