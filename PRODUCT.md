# Product Context

This file records the product facts that should shape interface decisions. It is
not a feature inventory or roadmap. Use
[`docs/planning/status.md`](docs/planning/status.md) for what exists now and
[`docs/planning/backlog.md`](docs/planning/backlog.md) for planned work.

## Product Promise

DMARC Analyzer is a self-hosted operations console for MSPs that
monitor DMARC across many clients and domains. It turns aggregate mail reports,
live DNS state, and delivery history into evidence an operator can use to find
unauthorized senders, improve authentication, and advance policy safely.

The product is successful when an operator can answer these questions quickly:

- Is report collection working?
- Which clients or domains need attention?
- Is a source legitimate, misconfigured, or suspicious?
- What evidence supports the next policy change?
- What changed, who changed it, and can the install be recovered?

Self-hosting is part of the trust model: report data, credentials, and client
relationships remain in infrastructure controlled by the operator.

## Users And Operators

### MSP administrator

Owns the install. Administrators manage clients, domains, report sources, users,
notification recipients, retention, backup and recovery, and other high-risk
settings. They need explicit scope, consequences, and recovery guidance before a
mutation.

### MSP analyst

Works across all clients. Analysts monitor collection health, investigate
threats and authentication failures, drill into sending sources, triage alerts,
and use the path-to-enforcement guidance. They need dense, repeatable workflows
with minimal navigation overhead.

### Client viewer

Has read-only access only to granted clients. Client viewers need understandable
results without losing the evidence behind them. Client scope is enforced by the
application, and unavailable resources should not leak through navigation,
counts, search, or error messages.

### Self-hosting operator

Deploys, upgrades, monitors, backs up, and recovers the service. This may be the
same person as the MSP administrator. They need the running version, source
health, durable audit evidence, configuration safety warnings, and honest
degraded states.

Most monitored clients do not need an account. Do not design every workflow as
if each client administers its own tenant.

## Core Workflows

### Establish or recover an install

Create the first administrator, then either start clean or restore a
configuration export while faithful restore is still possible. Make the
difference between restore and additive merge explicit. Credential-key mismatch,
plaintext credentials, session invalidation, and rows that require manual repair
must be visible before and after the action.

### Configure report collection

Create clients and domains, then add IMAP or API report sources. A source has an
authoritative default client; a machine-ingest caller selects a source, never a
client. Reveal-once API keys, mailbox credentials, retention deletion, routing,
and manual sync are security-sensitive operations rather than generic form
fields.

### Monitor the portfolio

The dashboard is the normal first screen. Its first viewport must make clear:

- how many domains are monitored;
- the current DMARC compliance rate and analyzed volume;
- how much spoofing was blocked;
- the reporting window, including when it is anchored to the newest data rather
  than wall-clock time;
- report-source health; and
- which domains need attention.

Summary data should lead directly to a filtered list or domain drill-down.

### Investigate a domain or source

Start from Domains, Threats, Alerts, or Dashboard and preserve the selected time
window and source in the URL. Show effective DMARC policy, where an inherited
policy came from, enforcement state, compliance, message volume, reporters,
sending IPs, authentication results, dispositions, live record inspection,
transport-security evidence, and the next safe enforcement step.

The interface must keep these distinctions legible:

- published policy versus effective inherited policy;
- DMARC alignment versus receiver disposition;
- no data versus `p=none`;
- a failed live lookup versus a confirmed missing record;
- suspicious mail versus a legitimate but misconfigured sender; and
- current evidence versus last-known evidence retained through a transient
  failure.

### Triage and communicate

Review automatically raised compliance drops and policy regressions, acknowledge
or close them, see whether notification was delivered, and manage MSP-wide or
client-specific recipients. A recorded alert and a successfully sent email are
separate outcomes.

### Operate and recover

Review mailbox health and sync history, trigger a bounded manual action, inspect
the immutable audit trail, export configuration, monitor offload status, and
restore or merge configuration. Recovery copy must explain what is included,
what can be re-ingested, and what cannot be restored from an archive yet.

## Product Model And Language

- **Client** is the tenant root. Use "client", not a mixture of customer,
  organization, workspace, and account.
- **Domain** is the primary monitored object and is globally owned by one client.
- **Report source** is an IMAP mailbox or API source. Use the broader term when
  both protocols are present; reserve "mailbox" for IMAP-only health and sync.
- **Sending source** is evidence about mail origin, usually an IP plus its
  authentication results. Do not confuse it with a report source.
- **Compliance** means DMARC-aligned DKIM or SPF. It is not the same as a receiver
  disposition.
- **Enforcement** describes the effective DMARC policy: enforced, ramping,
  spoofing, monitoring, or no data.
- **MSP** describes the operating model. Internal role identifiers retain their
  existing `agency_*` names for compatibility; do not expose that vocabulary in
  operator-facing copy.

Use precise technical values such as domains, IP addresses, policies, report
IDs, timestamps, and versions. Do not replace them with vague summaries when the
exact value is available.

## Product Tone

The console should feel calm, capable, technical, and evidence-led. It should
support repeated operational use rather than perform for a demo.

Copy should be:

- sentence case, plain, and direct;
- specific about the object, scope, time window, and consequence;
- honest about uncertainty and incomplete evidence;
- concise in tables, with fuller guidance beside risky actions; and
- free of hype, blame, jokes, emoji, and artificial urgency.

Prefer "No DMARC reports yet" over "You're all set!" and "The last offload pass
failed" over "Something went wrong." Reserve words such as critical, blocked,
secure, compliant, and restored for states the system can prove.

## Trust And Risk

Sensitive data includes client relationships, domains, report contents, sending
IPs, mailbox credentials, reveal-once API keys, notification addresses, user
identities, authentication history, and audit details. Tokens, passwords, and
report payloads must never appear in logs or routine screen history.

Trustworthy screens provide:

- source and client scope;
- exact counts and time windows;
- last attempt and last successful operation where those differ;
- persistent success, warning, and error feedback;
- a recovery or retry path when one exists;
- provenance for inherited or last-known values;
- explicit partial-success results; and
- the running application version for operational correlation.

High-risk actions must name what changes immediately and what remains unchanged.
Use confirmation and acknowledgment for destructive retention, credential
exposure, key revocation, restore/merge, and similar boundaries. Do not bury a
dangerous consequence in helper text or rely on color alone.

Loading, empty, filtered-empty, stale, unavailable, unauthorized, and failed are
different states. Never turn an absent response into a reassuring zero or a
green status.

## Anti-References

Avoid:

- a marketing landing page inside the authenticated console;
- generic startup dashboards made mostly of decorative cards;
- a single opaque security score or vanity grade;
- gamification, celebration effects, and fear-driven security copy;
- giant headings, excessive whitespace, and low-density mobile-first layouts on
  desktop;
- generic sample people or companies where real domain-shaped data explains the
  workflow better;
- hidden table actions, icon-only primary actions, and status communicated only
  by hue;
- automation that changes DNS, policy, routing, retention, or credentials
  without a scoped operator decision; and
- UI claims that confuse configured, attempted, delivered, deployed, or verified
  state.

The current console is the primary product reference. External DMARC products
may inform domain expectations, but they do not override this product's
MSP operating model, self-hosted trust boundary, or existing operational language.
