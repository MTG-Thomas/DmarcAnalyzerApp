# Prepared runtime session identity diagnostic

Status: source and tests prepared; not yet restored, compiled, run, reviewed or
activated. Heavy laptop tests are prohibited pending the owner-qualified remote
capacity. Do not treat this plan as runtime evidence or deployment approval.

The API/all host's existing /health/ready path uses its actual application
DbContext and Npgsql pool. One constant SELECT reads only session_user,
current_user and pg_catalog.current_database(), with a two-second command timeout.
The existing ready/unavailable HTTP response contains no identity values.
No endpoint, credential loading, separate diagnostic connection, role change,
DDL, retry, scheduler or status service is introduced. Database opening retains
the normal configured connection timeout; this is not a two-second total HTTP
request guarantee. Liveness and the separate worker/migrate/mta-sts modes remain
outside this diagnostic's coverage.

One application-owned singleton NpgsqlDataSource replaces EF's implicit data
source, preserving the resolved connection string. It serves the application's
normal DbContexts and is disposed with the host. A physical-connection initializer
records host/port/backend PID with a process-local generation, without running
SQL. Readiness captures that generation before its query. Only a later sample
on the same recorded generation can log one sanitized identity sample per API
process. Replacement or backend PID reuse resets the observation, and stale query
results cannot mark the replacement as cached. At most 128 physical keys are
tracked; unknown/untracked connections cannot yield a reuse claim.

This does not inventory every pool/replica, preserve pre-deployment sessions or
prove session continuity behind an intermediate transaction pooler. Deploying
this patch replaces old processes; observations then describe the new running
application's pooled sessions. No historical uncertainty is accepted on the
operator's behalf. Source tests and disposable PostgreSQL tests prove no
production identity. Unsafe names and provider errors are not emitted; the
caller's ordinary readiness failure result remains authoritative.

The proposed tests cover fresh versus reused physical generations, replacement,
stale samples, host separation, safe logging, and a real private test data source
which is cleared only in the fixture. No shared application pool is cleared by
the diagnostic. Tests must use an isolated synthetic PostgreSQL database, never
production connection strings. Existing integration fixtures create/drop only
their randomized disposable database.

Remote validation, after capacity/access/isolation and resource qualification:

    dotnet restore DmarcAnalyzerApp.slnx --locked-mode
    dotnet build DmarcAnalyzerApp.slnx --configuration Release --no-restore -warnaserror -m:1
    dotnet test src/api.tests/DmarcAnalyzer.Api.Tests.csproj --configuration Release --no-build
    dotnet test src/api.integrationtests/DmarcAnalyzer.Api.IntegrationTests.csproj --configuration Release --no-build --filter FullyQualifiedName~DatabaseReadinessProbeTests

The repository pins .NET SDK 10.0.400 (latestPatch, no prerelease). The laptop has
only SDK 8 and was not changed. The target must supply the qualified SDK or a
verified published SDK image digest, required network access to public package
sources, locked dependencies, and private owner-specific build/output/cache
paths. Do not copy production secrets, whole private caches or encryption keys.
No package/lockfile change is part of this patch; no UI files changed.

Actual target: app-mtg-dmarc-production, existing application process and pool.
Normal source/security review, qualified published image/provenance, protected
maintenance/deployment and any process replacement require their own approval.
No runtime security setting, guest resource, credential, grant, database or
production image pin has been changed by this preparation. BiFrost #2139 remains
draft/no-apply; #2136's GitGuardian security review and ARM status-read proof are
separate unresolved gates.

Driver contracts used in this design:
- https://www.npgsql.org/doc/api/Npgsql.NpgsqlDataSourceBuilder.html
- https://www.npgsql.org/doc/api/Npgsql.NpgsqlDataSource.html
- https://www.npgsql.org/efcore/ (explicit data source avoids untracked varying
  ConfigureDataSource callbacks being shared by EF's implicit cache).
