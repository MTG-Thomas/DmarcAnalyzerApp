# MTG Managed-Service Vision

Last reviewed: 2026-08-11.

This document extends the upstream product direction with MTG's agency-managed
DMARC service goals. It is a fork planning artifact, not an upstream commitment
or permission to use customer data. Delivery still starts with the pre-live
gates in [`mtg-fork-slices.md`](mtg-fork-slices.md).

## Outcome

Make DmarcAnalyzer the system of record and operator console for an MTG-managed
email-authentication service: onboard a client portfolio, identify every
legitimate sender, move domains safely to enforcement, keep them there, and
produce evidence an operator or client can act on.

The target is the useful operating model of a managed platform, not a feature-
for-feature clone. EasyDMARC's current MSP surface is the benchmark because it
combines multi-client operations, reporting, managed DNS records, integrations,
and automation. Its public API also exposes domain groups, reports, DNS checks,
alerts, and webhooks. The earlier Bifrost research reached the same practical
goal through sender triage, DNS work packets, findings, and policy milestones.

References:

- [EasyDMARC MSP platform](https://easydmarc.com/dmarc-for-msps)
- [EasyDMARC feature guide](https://support.easydmarc.com/knowledge-base/easydmarc-features-guideline)
- [EasyDMARC API overview](https://developers.easydmarc.com/get-started/)
- [Bifrost FOSS landscape and app plan](https://github.com/MTG-Thomas/bifrost-workspace/blob/b715139ff4d7079286daf2cf8899c1b503701bfc/docs/plans/2026-03-29-dmarc-foss-landscape-and-bifrost-app-plan.md)

## Alignment with upstream v0.10.0

The upstream application is already a strong analyzer and policy-safety base.
The remaining distance is mostly managed-service workflow, not report parsing.

| Capability | Upstream/fork position | MTG direction |
| --- | --- | --- |
| Agency tenancy, RBAC, OIDC, client read-only access, audit | Shipped core; portal polish remains | Reuse as the access boundary |
| RUA ingestion, analytics, source drill-down, threat view | Shipped core | Reuse; add durable sender identity and classification |
| Enforcement guidance and live DMARC/SPF inspection | Shipped core | Extend into approved change plans, milestones, and verification |
| Alerts and monthly digests | Shipped core | Route actionable events into operator queues, webhooks, and HaloPSA |
| MTA-STS hosting/monitoring and TLS-RPT | Shipped core | Include in the managed domain posture and reporting model |
| RUF, BIMI, reputation/geo, richer source identification | Already planned | Deliver only after the managed RUA loop is proven |
| CSV/JSON export, branded PDF, client reporting | Already planned in parts | Treat white-label reporting as a managed-service requirement |
| Microsoft Graph and Gmail acquisition | Already planned as app connectors | For MTG, keep Graph credentials, retry, and raw evidence in `bifrost-workspace` and use the private source-scoped ingest API |
| Portfolio groups/tags, bulk actions, onboarding state | Missing as a coherent capability | Add to the application model and operator UI |
| Sender catalog, finding ownership, snooze/resolve workflow | Threat/enforcement views provide inputs but not lifecycle | Add one durable operator workflow shared by alerts and remediation |
| Managed DMARC/SPF/DKIM changes and DNS integrations | Inspection exists; MTA-STS is the only hosted record path | Add preview, approval, apply, rollback, and public-DNS readback through delegated adapters |
| PSA/webhook/billing integrations | Mostly absent | Prioritize signed webhooks and HaloPSA; keep billing/marketplace outside the analyzer until needed |

In short: protocol coverage and analytics align strongly; agency controls align
well; managed remediation is partial; portfolio automation and MSP ecosystem
integration are the largest gaps.

## Product boundary

Avoid rebuilding the earlier Bifrost-native DMARC control plane beside this
application. The durable split is:

- **DmarcAnalyzerApp:** clients, domains, sources, reports, sender identities,
  findings, policy milestones, approvals, audit, analytics, and client-facing
  reporting.
- **bifrost-workspace:** Microsoft Graph mailbox acquisition, bounded retry and
  outbox behavior, raw-attachment retention/replay, HaloPSA workflows, and DNS
  provider orchestration.
- **bifrost-infra:** AKS, managed PostgreSQL, Key Vault, private networking,
  backups, digest selection, and deployment evidence.

Integrations call versioned, source-scoped application APIs and consume signed,
idempotent events. Request data may select a source, never a client. Customer
DNS credentials do not move into this repository merely to make an integration
look native.

## Delivery sequence

### 1. Prove the product core

Complete the fork dogfood slices, deploy one private synthetic/internal source,
and prove migration, replay, isolation, backup/restore, and rollback. Do not
start a second ingestion or persistence path for managed-service features.

### 2. Add the managed operator loop

- Portfolio groups/tags, bounded bulk selection, and onboarding/offboarding
  state.
- A sender catalog with `authorized`, `unknown`, `forwarded`, `threat`, and
  `ignored` classifications, first/last seen, evidence, and operator notes.
- A finding lifecycle with owner, severity, status, snooze, resolution note,
  and links to the source evidence and affected policy milestone.
- Fleet views that answer what changed, what is blocking enforcement, who owns
  the next action, and which domains lack fresh RUA evidence.

Use the existing threat, enforcement, alert, audit, and client/domain models as
inputs. One sender/finding lifecycle is enough; do not add parallel case types
for every alert rule.

### 3. Make remediation safe and reportable

- Persist proposed DNS changes with before/after values, reason, approver,
  expiry, apply result, rollback data, and authoritative public-DNS readback.
- Let Bifrost adapters apply an approved change through a supported DNS lane;
  otherwise export a human work packet and verify the result the same way.
- Add white-label report settings, CSV/JSON exports, and branded PDF summaries
  from the existing analytics and digest data.
- Emit signed, replay-safe events for new senders, policy regressions, missing
  reports, and verified milestones; make HaloPSA the first consumer.

DNS writes remain preview-first and explicitly approved. A successful provider
call is not completion until public DNS and subsequent report evidence agree.

### 4. Expand protocol and enrichment coverage

- RUF ingestion only with an explicit privacy, redaction, retention, and client
  consent model.
- BIMI readiness and artifact status after DMARC enforcement is stable.
- Known-vendor guidance, ASN/geography, and reputation enrichment with evidence
  timestamps and failure-tolerant providers.
- DKIM selector inventory and rotation tracking; SPF lookup-budget diagnostics
  before any automated record rewriting.

Dynamic SPF flattening is not an early parity checkbox. Add it only if real
clients need it and the design has freshness monitoring, deterministic rollback,
provider outage behavior, and an explicit approval boundary.

## Acceptance for a managed pilot

A private pilot is ready when an operator can:

1. onboard one client and its domains without editing application tables;
2. ingest or replay a retained report through a source-scoped path;
3. classify senders and work a finding to a verified resolution;
4. advance a domain through an approved, reversible policy milestone without
   losing legitimate mail;
5. receive a branded summary and a HaloPSA action with links back to evidence;
6. offboard the client and prove the configured data-retention outcome; and
7. restore the service and its configuration from tested backups.

Sales enablement, marketplace billing, lead generation, and a large integration
catalog are not pilot gates. Add them when the managed service has repeatable
delivery and an actual commercial owner.
