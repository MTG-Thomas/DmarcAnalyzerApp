// CI gate around `npm audit`: fail on high/critical findings except advisories
// explicitly allowlisted in ../npm-audit-allowlist.json.
//
// An entry is a deliberate, reviewable risk acceptance (dev-only build tooling
// with no production attack surface, tracked follow-up issue), not a way to
// hide findings: anything not on the list still fails the build.
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const FAIL_SEVERITIES = new Set(['high', 'critical']);
const ALLOWLIST_PATH = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '../npm-audit-allowlist.json',
);

export function ghsaIdFromUrl(url) {
  const match = /\/GHSA-[a-z0-9-]+$/i.exec(url ?? '');
  return match ? match[0].slice(1).toLowerCase() : null;
}

// Pure filter over `npm audit --json` output so it stays testable without the
// network. npm attributes each advisory (with URL) to the vulnerable
// package's own entry; dependent packages in the chain reference it by name
// only, so those echo entries carry no independent signal. The gate therefore
// works at advisory granularity: it fails iff any advisory attached to a
// high/critical finding is not allowlisted. A high/critical finding with an
// empty trail fails safe, since there is nothing to disposition.
export function evaluateAudit(auditJson, allowlist) {
  const vulnerabilities = auditJson?.vulnerabilities ?? {};
  const seen = new Map();
  const trailless = [];
  for (const [name, finding] of Object.entries(vulnerabilities)) {
    if (!FAIL_SEVERITIES.has(finding?.severity)) continue;
    const via = finding?.via ?? [];
    if (via.length === 0) {
      trailless.push(name);
      continue;
    }
    for (const item of via) {
      if (typeof item === 'object' && item !== null && item.url) {
        const id = ghsaIdFromUrl(item.url);
        if (!seen.has(id)) {
          seen.set(id, { id, title: item.title ?? '', url: item.url, packages: [] });
        }
        if (!seen.get(id).packages.includes(name)) {
          seen.get(id).packages.push(name);
        }
      }
    }
  }
  const covered = [];
  const uncovered = [];
  for (const advisory of seen.values()) {
    if (advisory.id !== null && allowlist[advisory.id] !== undefined) {
      covered.push({ ...advisory, reason: allowlist[advisory.id].reason });
    } else {
      uncovered.push(advisory);
    }
  }
  for (const name of trailless) {
    uncovered.push({ id: null, title: 'high/critical finding with no audit trail', url: '', packages: [name] });
  }
  return { covered, uncovered };
}

function loadAuditJson() {
  const auditFile = process.argv.find((a) => a.startsWith('--audit-file='))?.split('=')[1];
  if (auditFile) {
    return JSON.parse(readFileSync(auditFile, 'utf8'));
  }
  const webDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const result = spawnSync('npm', ['audit', '--json'], {
    cwd: webDir,
    encoding: 'utf8',
    maxBuffer: 32 * 1024 * 1024,
  });
  // npm audit exits non-zero when findings exist; the JSON on stdout is still
  // the report. Only empty/garbled output is a hard error.
  try {
    return JSON.parse(result.stdout);
  } catch {
    console.error(`::error::npm audit produced no parseable JSON (exit ${result.status})`);
    console.error((result.stderr ?? '').slice(-2000));
    process.exit(2);
  }
}

function main() {
  const allowlist = JSON.parse(readFileSync(ALLOWLIST_PATH, 'utf8'));
  const { covered, uncovered } = evaluateAudit(loadAuditJson(), allowlist);
  for (const c of covered) {
    console.log(`::warning::npm audit: ${c.id} allowlisted (via ${c.packages.join(', ')})`);
  }
  if (uncovered.length > 0) {
    for (const u of uncovered) {
      console.error(`::error::npm audit: ${u.id ?? 'untraceable finding'} not allowlisted (via ${u.packages.join(', ')})`);
    }
    console.error(`npm audit gate failed: ${uncovered.length} non-allowlisted high/critical advisories`);
    process.exit(1);
  }
  console.log(
    `npm audit gate passed (${covered.length} allowlisted high/critical advisories, 0 unlisted)`,
  );
}

if (process.argv[1] !== undefined && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
