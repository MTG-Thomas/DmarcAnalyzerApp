# Prepared runtime session identity diagnostic

Status: source validated on an isolated remote guest; review and production
activation remain pending. At `cb65b9f2ca4f16135e1500e25272e78bb8073c4c`, locked
restore and the Release warnings-as-errors build passed with zero warnings and
errors; all 1,216 unit tests and the real PostgreSQL pool replacement/reuse test
passed with no skips. The guest was bounded to two CPUs and 4 GiB; the SDK
container used at most 1.5 CPUs / 3 GiB, and PostgreSQL 0.5 CPU / 512 MiB. Test
services were stopped afterward; evidence and volumes were retained. No laptop
Docker test batch, production query or activation is part of this evidence.

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

The tests cover fresh versus reused physical generations, replacement,
stale samples, host separation, safe logging, and a real private test data source
which is cleared only in the fixture. No shared application pool is cleared by
the diagnostic. Tests must use an isolated synthetic PostgreSQL database, never
production connection strings. Existing integration fixtures create/drop only
their randomized disposable database.

Remote validation commands (passed after capacity/access/isolation qualification):

    dotnet restore DmarcAnalyzerApp.slnx --locked-mode
    dotnet build DmarcAnalyzerApp.slnx --configuration Release --no-restore -warnaserror -m:1
    dotnet test src/api.tests/DmarcAnalyzer.Api.Tests.csproj --configuration Release --no-build
    dotnet test src/api.integrationtests/DmarcAnalyzer.Api.IntegrationTests.csproj --configuration Release --no-build --filter FullyQualifiedName~DatabaseReadinessProbeTests

The repository pins .NET SDK 10.0.400 (latestPatch, no prerelease). The laptop has
only SDK 8 and was not changed. The target must supply the qualified SDK or a
verified published SDK image digest, required network access to public package
sources, locked dependencies, and private owner-specific build/output/cache
paths. Do not copy production secrets, whole private caches or encryption keys.
The first build exposed nullable Npgsql host annotations and audited transitive
cryptography dependencies. Missing hosts now fail closed without a reuse claim.
`System.Security.Cryptography.Pkcs` is pinned to patched 10.0.13 in the API;
`System.Security.Cryptography.Xml` is pinned to patched 10.0.13 only in the two
non-Web test hosts (the API uses the shared framework). Locks were regenerated,
then locked restore and the original warnings-as-errors build passed. Audit and
warning policy are unchanged. See the [PKCS advisory](https://github.com/advisories/GHSA-846f-f764-76mx)
and [XML advisory](https://github.com/advisories/GHSA-7hqp-7c5m-4vq6).
No UI components or application behavior files changed. CI at `0c0fe36` passed
PostgreSQL 16 integration, PostgreSQL 18 migration smoke, CodeQL and Sonar, but
stopped at the frontend audit gate on existing vulnerable dependencies. A targeted
lock refresh within existing declared ranges updates Vite to 8.3.5, Undici to
8.12.3 and Zod to 4.6.7 with their required dependency closure. Audit policy and
the allowlist are unchanged. The same isolated remote guest, using Node 22.23.4
in pinned image `node@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914`,
passed `npm ci`, `npm run audit:ci` (zero unlisted high/critical advisories),
`npm run lint`, `npx tsc --noEmit`, all 139 tests across 22 files with one bounded
test worker, and `npm run build`. Test services stopped afterward. These checks
covered the exact lock patch on `0c0fe36`; fresh final-candidate CI, including the
container image build and broader suites, remains required.

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
