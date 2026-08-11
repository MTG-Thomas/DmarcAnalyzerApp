# DMARC conformance corpus handoff

This directory owns the deterministic recipe and contract-only test for the
Analyzer conformance corpus. Generated raw payloads and normalized expected
state live under `../Fixtures/Conformance`.

Provenance is recoverable one-to-one through each manifest case's
`source_case_id`:

- repository: `MTG-Thomas/bifrost-infra`
- commit: `258d606e1f8ce859fff5422d550d469c3b666111`
- recipe: `scripts/generate-dmarc-bakeoff-corpus.py`
- prior manifest/expected schemas: `tests/fixtures/dmarc-bakeoff/`
- prior PostgreSQL projection: `scripts/sql/dmarc-bakeoff-analyzer-projection.sql`
- prior comparison tool: `scripts/collect-compare-dmarc-bakeoff.py`

The port keeps all 33 source case IDs and 9 immediate recovery sentinels. It
emits 35 raw attachment payloads rather than EML envelopes and pins one Analyzer
expected outcome per case rather than retaining candidate alternatives.
For the raw `null-report` extension, empty `policy_evaluated` values normalize
to `none` disposition and `fail` DKIM/SPF in expected PostgreSQL state. This is
the Analyzer parser's intentional conservative default: missing data must not
manufacture compliance.

Generate and verify:

```powershell
python src/api.integrationtests/Conformance/generate_conformance_corpus.py
python src/api.integrationtests/Conformance/generate_conformance_corpus.py --check
```

This corpus-data commit intentionally stops before the final Slice 6 runner. A
follow-up must invoke `IReportPayloadIngestor` directly against disposable real
PostgreSQL and compare exact report, record, DKIM, SPF, ingest-ledger, source,
and client state. Slice 5 endpoint authentication/authorization assertions must
reuse that same production path instead of reimplementing ingestion.
