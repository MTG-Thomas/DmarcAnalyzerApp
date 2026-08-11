# PostgreSQL integration tests

This project is the real-database lane for migrations, raw SQL, transactions,
and constraints. It complements `src/api.tests`; it does not replace the fast
EF InMemory suite.

Set `DMARC_TEST_POSTGRES` to an administrative connection string for a
disposable PostgreSQL server, then run:

```powershell
$env:DMARC_TEST_POSTGRES = 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=<test-only password>'
dotnet test src/api.integrationtests/DmarcAnalyzer.Api.IntegrationTests.csproj
```

Each xUnit collection creates a randomly named database and drops it on
completion. Never point the variable at a server where the test identity cannot
safely create and drop disposable databases.

The previous-release migration test currently targets the same latest migration
for v0.9.0 and v0.10.0. That upgrade is intentionally a no-op until the next
schema-bearing release, while still proving seeded configuration survives and
the current model has no pending migration.

Direct DMARC report/record/auth-result/ledger atomicity and replay remain outside
this harness PR because those writes are still private to `MailboxSyncService`
behind a concrete IMAP connection. Slice 2 moves those assertions onto the
extracted parsed-report ingestor using this fixture.
