using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

public static class PostgreSqlCollections
{
    public const string Migrations = "PostgreSQL migrations";
    public const string Persistence = "PostgreSQL persistence";
}

[CollectionDefinition(PostgreSqlCollections.Migrations)]
public sealed class PostgreSqlMigrationCollection
    : ICollectionFixture<PostgreSqlDatabaseFixture>;

[CollectionDefinition(PostgreSqlCollections.Persistence)]
public sealed class PostgreSqlPersistenceCollection
    : ICollectionFixture<PostgreSqlDatabaseFixture>;
