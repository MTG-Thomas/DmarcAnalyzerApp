# DmarcAnalyzerApp

MTG working fork of the ASP.NET Core/Carter, React/Vite, PostgreSQL DMARC analyzer. Application/parser/schema/chart changes belong here; AKS/PostgreSQL/Key Vault deployment belongs in `bifrost-infra`, and Graph acquisition/retry/raw retention in `bifrost-workspace`.

For setup/worktrees, read [build and run](docs/agent-guidance.md#build-test-run); for schema/API work, [architecture references](docs/agent-guidance.md#architecture--conventions--where-to-read); for ingestion, [code-review rules](docs/agent-guidance.md#code-review-rules) and [fork delivery gates](docs/planning/mtg-fork-slices.md). Settings changes use [configuration](docs/ops/configuration.md); stacked PRs use [working agreements](docs/agent-guidance.md#working-agreements). Load only the sections relevant to the task.

## Work and verification

Use a separate worktree for tracked edits; stage explicit paths. Protected `main` receives PRs. Preserve the squash/stacked-PR retargeting procedure in the detailed guide. Update planning status/backlog for features.

From root: `dotnet restore DmarcAnalyzerApp.slnx --locked-mode`, `dotnet build DmarcAnalyzerApp.slnx --configuration Release --no-restore -warnaserror`, and `dotnet test src/api.tests/DmarcAnalyzer.Api.Tests.csproj --configuration Release --no-build`. In `src/web`: `npm ci`, `npm run lint`, `npm test`, `npm run build`. Follow CI's PostgreSQL setup for `dotnet test src/api.integrationtests/DmarcAnalyzer.Api.IntegrationTests.csproj --configuration Release`. UI changes also need the real local app. Never disable locked restore to fix lockfile drift.

## Critical invariants

- Tenant root is client; authenticated source determines client. Machine callers cannot override tenancy. Keep modules thin and queries tenant-scoped.
- Client-viewer and machine endpoints are deny-by-default. Never broaden session public paths or log tokens/report payloads.
- Bound request/archive/decompressed sizes, entry count, and compression ratio with rejection tests. Both mailbox/upload use one raw-ingestion orchestrator.
- Persistence/dedup/transaction/migration changes require real PostgreSQL evidence; InMemory is insufficient. Exactly one ingestion worker per database.
- Preserve the existing mailbox encryption key when preparing worktrees; never regenerate it against an existing database.
- MTG images come from tested fork CI commits and infrastructure pins digests. Customer data/public ingress/production claims require pre-live gates; release tags and upstream submissions require explicit operator decisions.
