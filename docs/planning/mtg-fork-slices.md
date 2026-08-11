# MTG Fork Dogfood Slices

Last updated: 2026-08-11.

This is the fork-owned delivery plan for taking DmarcAnalyzer from the completed
synthetic bake-off to a private MTG dogfood deployment. It supplements the
upstream backlog; it does not redefine the upstream product roadmap.

The bake-off selected this fork over DMARCGuard, proved the existing Helm chart
against AKS and managed PostgreSQL 16, and found no reason to add Talos or
CloudNativePG. It did **not** approve customer data, public ingress, or a
production claim. The durable decision is in
[`MTG-Thomas/bifrost-infra`](https://github.com/MTG-Thomas/bifrost-infra/commit/258d606e1f8ce859fff5422d550d469c3b666111).

## Delivery rules

- Each numbered slice is independently reviewable and leaves `main` releasable.
- Code-bearing work lands in the fork and passes its pull-request checks before
  the merge commit may publish an `edge`/`sha-*` image. Infrastructure selects
  only the resulting digest.
- No release tag, upstream issue, upstream pull request, customer-data run, or
  public endpoint is implied by this plan. Each needs an explicit decision.
- Parser, persistence, source/authentication, and chart behavior belong here.
  AKS/PostgreSQL/Key Vault deployment belongs in `bifrost-infra`; Microsoft
  Graph acquisition, retry, and raw-attachment retention belong in
  `bifrost-workspace`.
- `MailboxSourceId` remains the report provenance key for these slices. An API
  source is represented by a source row; broad source-table renaming and
  nullable provenance foreign keys are out of scope.

## Target seams

The exact record members may evolve in implementation, but the dependency
direction is fixed:

```csharp
Task<DmarcIngestResult> IngestParsedAsync(
    ReportSourceContext source,
    DmarcReportParseResult report,
    CancellationToken ct);

Task<ReportPayloadIngestResult> IngestAsync(
    ReportSourceContext source,
    Stream payload,
    ReportPayloadMetadata metadata,
    CancellationToken ct);
```

`ReportSourceContext` is built by trusted IMAP or API-auth code and is never
deserialized from a request. Parsed persistence owns domain routing,
transactionality, deduplication, and report/record/ledger writes. Raw-payload
ingestion owns bounded extraction, format routing, parsing, and dispatch. It
knows neither MailKit nor HTTP authentication.

## Slice sequence

| Slice | Deliverable | Depends on | Pre-live gate |
| --- | --- | --- | --- |
| 0 | Complete RFC 9990 `pass` analytics/UI support | none | semantic completeness |
| 1 | Real PostgreSQL integration lane | none | migrations, atomicity, concurrency |
| 2 | Extract parsed DMARC persistence service | 1 | one tested persistence path |
| 3 | Add bounded raw-payload ingestion orchestrator | 2 | bounded containers and parsing |
| 4 | Add API report-source model and reveal-once keys | 1 | source-scoped machine identity |
| 5 | Add authenticated raw upload endpoint | 3, 4 | safe internal machine ingestion |
| 6 | Make the synthetic Analyzer corpus a CI gate | 1, 3, 5 | replay, isolation, routing, limits |
| 7 | Integrate the Bifrost Graph adapter privately | 5, 6 | end-to-end dogfood readiness |

Slices 0 and 1 may run in parallel. The bounded extractor portion of slice 3
may also be authored in parallel, but the raw orchestrator must merge after
slice 2 so the HTTP edge never invents a second parser or persistence path.

## 0. RFC 9990 `pass` analytics and UI

Status: implemented in the fork on 2026-08-11.

The parser, database, analytics DTOs and query projections, TypeScript contract,
and source-detail UI preserve `policy_evaluated.disposition=pass` end to end.
Compliance remains aligned-DKIM/SPF based, while blocked totals remain
`quarantine + reject`.

PR boundary:

- add a `Pass` bucket to both analytics projections and API DTOs;
- add it to the TypeScript model and every disposition visualization/total;
- preserve existing compliance calculations, which derive from DKIM/SPF
  alignment rather than disposition;
- keep "spoofing blocked" as `quarantine + reject`; `pass` is not a blocked
  disposition; and
- add backend query tests plus frontend render tests for a non-zero `pass`
  bucket.

Acceptance: a namespaced RFC 9990 report persists and displays its message count
as `pass`, all disposition buckets sum to the expected total, and existing
DMARC v1 results are unchanged.

Upstream disposition: a small, concrete upstream candidate after the fork PR is
green and dogfood evidence exists.

## 1. Real PostgreSQL integration lane

The current EF InMemory suite cannot prove raw SQL, transactions, unique-index
behavior, or migrations. Add a separate real-PostgreSQL category and CI job
without replacing fast unit tests.

PR boundary:

- start PostgreSQL 16 in CI with an isolated database per test collection;
- apply all migrations to an empty database and verify the expected latest
  migration;
- prove a supported-version upgrade from the previous release schema;
- verify report, record, auth-result, and ingest-ledger writes are atomic;
- verify duplicate replay creates no child rows, including two concurrent
  attempts; and
- run the same migration smoke test against the next supported PostgreSQL major
  when CI cost remains reasonable.

Acceptance: the integration job is required for ingestion/schema pull requests,
reports connection material only through masked CI secrets, and fails on a
partial write, duplicate child row, or migration drift.

Upstream disposition: upstream candidate; it closes a known testing gap without
changing product behavior.

## 2. Parsed DMARC persistence service

Extract the transaction, domain resolution, deduplication, and entity writes
from `MailboxSyncService` behind `IDmarcReportIngestor`, mirroring the existing
`ITlsReportIngestor` shape. This is a refactor, not the final upload seam.

PR boundary A — behavior-preserving extraction:

- accept a trusted source context plus one parsed DMARC report;
- return inserted/duplicate/rejected detail without leaking persistence types;
- keep the existing domain-scoped report identity and non-null source
  provenance; and
- move the slice-1 atomicity, rollback, replay, and concurrency assertions onto
  this service.

PR boundary B — routed ownership correction:

- when an existing domain belongs to a different client than the source's
  default, persist the report and ingest ledger against the domain owner; and
- prove the source cannot reassign the existing domain or expose it through the
  default client.

Acceptance: mailbox counters/checkpoint behavior and schema are unchanged,
`MailboxSyncService` no longer owns DMARC database writes, the routed-owner
correction is explicit, and all persistence outcomes are proven on PostgreSQL.

Upstream disposition: upstream candidate; the upstream backlog already
anticipates this seam.

## 3. Bounded raw-payload ingestion orchestrator

Add `IReportPayloadIngestor` as the one deep entry point used by mailbox and
future HTTP callers. It owns format classification, bounded container
extraction, parser selection, and composition of the DMARC/TLS persistence
services.

The input is a trusted source context plus a stream, file name, and optional
media type. The result summarizes inserted, duplicate, and rejected DMARC/TLS
reports.

PR boundary A — bounded extractor (implemented 2026-08-11):

- classify from content/magic as well as labels;
- support bare XML/JSON, GZIP, and multi-entry ZIP;
- enforce configuration-backed request bytes, expanded bytes, archive entry
  count, per-entry bytes, and compression-ratio limits while streaming;
- reject encrypted, corrupt, empty, unsupported, nested, and limit-exceeding
  containers deterministically; and
- expose the standalone bounded contract without changing mailbox, parser,
  persistence, backup/import, or HTTP behavior; callers move onto it in the
  dependent orchestrator boundary.

PR boundary B — orchestrator (after slice 2):

- compose the bounded extractor, DMARC/TLS parsers, and parsed ingestors;
- make IMAP obtain bytes/metadata and map structured outcomes to its existing
  counters without owning persistence; and
- preserve checkpoint, archive-before-parse, timeout, and retry behavior.

Acceptance tests cover mislabeled input, junk before a valid ZIP entry, multiple
valid entries, exact and cross-container replay, corrupt/truncated containers,
external entities, and compression bombs. No test may require allocating the
declared expanded size.

Upstream disposition: split into small upstream candidates (bounded extractor,
then orchestrator) after fork proof.

## 4. API report source and reveal-once keys

Represent machine ingestion as a source with `Protocol=api` and an authoritative
`DefaultClientId`. Keep current non-null report/source foreign keys. API rows are
excluded from mailbox polling, mailbox-health failure calculations, and mailbox
retention actions.

PR boundary A — source model:

- permit an API source without pretending it has an IMAP host/user/password;
- retain source activation, client ownership, audit fields, and backup/export
  behavior; and
- add a focused, reversible migration.

PR boundary B — key lifecycle:

- add a credential table keyed to the source so two keys may overlap during a
  safe rotation;
- generate a high-entropy reveal-once token;
- persist only prefix plus SHA-256 hash and created/revoked timestamps;
- compare hashes in fixed time; and
- add agency-admin create/rotate/revoke operations with audit events and tests.

Acceptance: the raw token is never returned after creation, stored, logged, or
included in backup artifacts. A restored source requires token reissue.
Revoked/inactive/wrong-source keys receive the same authentication failure.

Upstream disposition: fork-first product capability; propose upstream only after
the contract is stable in dogfood.

## 5. Authenticated raw upload endpoint

Add `POST /api/ingest/v1/sources/{sourceId}/reports` as an internal-first machine
endpoint. Dedicated middleware or endpoint metadata authenticates the API key;
the route is deliberately outside the cookie-session `/api/v1` surface and is
never added to `SessionAuthMiddleware.PublicPaths`.

Contract:

- raw body or one multipart file, streamed into `IReportPayloadIngestor`;
- the authenticated source determines the client; no request `clientId`;
- `X-Content-SHA256` and `Idempotency-Key: sha256:<digest>` are checked against
  server-computed content when present;
- `201` when at least one report is inserted, `200` for duplicate-only replay;
- uniform `401` for invalid source/key state, `413` for limits, `415` for
  unsupported media, and `422` for a bounded payload that contains no valid
  report; and
- upload-specific audit events and low-cardinality metrics without token,
  payload, reporter email, or customer-domain log fields.

Acceptance: cookie authentication cannot bypass machine authentication; source A
cannot address source B or route to client B; replay is idempotent; partial
container outcomes are explicit; cancellation/rollback leaves no partial child
rows.

Upstream disposition: fork-first, then a contract-focused upstream proposal or
pull request only with operator approval.

## 6. Analyzer conformance corpus in CI

Promote the synthetic cases that selected the fork into a repo-owned,
deterministic Analyzer acceptance suite. Fixtures contain only `.example`
identities and documentation IP ranges, with a manifest and normalized expected
results.

PR boundaries:

1. transport and parser cases (v1/v2, bare/GZIP/ZIP, mislabeled and multi-entry);
2. persistence cases (exact replay, cross-container replay, conflicting
   duplicate, the same business key across sources, same report ID across
   domains, existing-domain owner routing, concurrent duplicate);
3. tenancy/source routing and invalid-input recovery sentinels; and
4. resource-limit cases that prove bounded rejection without constructing a
   dangerous archive in CI.

Acceptance: every case has stable provenance and expected output; failures are
attributed per case; an invalid/resource case must be followed by a unique valid
sentinel; the suite exercises the same application ingestion service as runtime
callers and reads final state from PostgreSQL.

Upstream disposition: upstream the generic fixtures/harness in reviewable
slices; keep MTG-specific routing cases in the fork.

## 7. Private Bifrost Graph adapter

This slice is implemented outside this repository after slices 5 and 6 pass.
`bifrost-workspace` continues to own Microsoft Graph credentials, mailbox
pagination/delta state, retries, and raw attachment retention. It forwards one
attachment to the internal source-scoped endpoint with content SHA-256
idempotency and provenance headers. It must not move Graph access or tenant
credentials into DmarcAnalyzer.

`bifrost-infra` owns the private AKS Service/NetworkPolicy, digest pin, managed
PostgreSQL database/role, and Key Vault delivery. Start with ClusterIP only and
one synthetic/internal mailbox source.

Acceptance: a retained raw attachment can be replayed; permanent 4xx outcomes
do not retry forever; 429/5xx outcomes use the workspace outbox/retry policy;
source/client isolation is proven; backup/restore and rollback evidence exist;
and no public ingress or customer mailbox is involved.

## Dogfood release gate

All of the following are required before recreating a persistent deployment:

- slices 0 through 6 green on the exact fork commit and published image digest;
- clean PostgreSQL 16 migration plus upgrade, rollback, and concurrent replay
  evidence;
- bounded body/archive enforcement and recovery sentinels;
- source-scoped key rotation/revocation and cross-client isolation evidence;
- backup/restore proof for the dedicated database and encryption key;
- private-only AKS exposure, default-deny policy, bounded resources, and
  restart/OOM evidence; and
- a separate operator decision naming the mailbox, data class, retention,
  rollback, and digest to deploy.

Talos, CloudNativePG, public ingress, and customer data remain out of scope
unless a recorded failure of the regular AKS + managed PostgreSQL path justifies
a new decision.
