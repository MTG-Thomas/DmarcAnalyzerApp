// CI gate around `npm audit`: fail on high/critical findings except advisories
// explicitly allowlisted in ../npm-audit-allowlist.json.
//
// An entry is a deliberate, reviewable risk acceptance (dev-only build tooling
// with no production attack surface, tracked follow-up issue), not a way to
// hide findings: anything not on the list still fails the build.
//
// Usage: npm audit --json | node scripts/npm-audit-gate.mjs
// Exit 0 when every high/critical advisory is allowlisted, 1 when one is not,
// 2 when the report itself is missing or unparseable.
import { fstatSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const FAIL_SEVERITIES = new Set(['high', 'critical']);
const ALLOWLIST_PATH = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '../npm-audit-allowlist.json',
);

export class GateFailure extends Error {
  constructor(exitCode, message) {
    super(message);
    this.exitCode = exitCode;
  }
}

export function ghsaIdFromUrl(url) {
  const match = /\/GHSA-[a-z0-9-]+$/i.exec(url ?? '');
  return match ? match[0].slice(1).toLowerCase() : null;
}

// npm attributes each advisory (with URL) to the vulnerable package's own
// entry; dependent packages in the chain reference it by name only, so those
// echo entries carry no independent signal.
export function collectAdvisories(vulnerabilities) {
  const seen = new Map();
  const trailless = [];
  for (const [name, finding] of Object.entries(vulnerabilities ?? {})) {
    if (!FAIL_SEVERITIES.has(finding?.severity)) continue;
    const via = finding?.via ?? [];
    if (via.length === 0) {
      trailless.push(name);
      continue;
    }
    for (const item of via) {
      if (typeof item === 'object' && item !== null && item.url) {
        recordAdvisory(seen, name, item);
      }
    }
  }
  return { advisories: [...seen.values()], trailless };
}

function recordAdvisory(seen, packageName, item) {
  const id = ghsaIdFromUrl(item.url);
  if (!seen.has(id)) {
    seen.set(id, { id, title: item.title ?? '', url: item.url, packages: [] });
  }
  const entry = seen.get(id);
  if (!entry.packages.includes(packageName)) {
    entry.packages.push(packageName);
  }
}

export function partitionAdvisories(advisories, allowlist) {
  const covered = [];
  const uncovered = [];
  for (const advisory of advisories) {
    if (advisory.id !== null && allowlist[advisory.id] !== undefined) {
      covered.push({ ...advisory, reason: allowlist[advisory.id].reason });
    } else {
      uncovered.push(advisory);
    }
  }
  return { covered, uncovered };
}

// A high/critical finding with an empty trail fails safe: there is nothing
// to disposition, so it stays uncovered.
export function evaluateAudit(auditJson, allowlist) {
  const { advisories, trailless } = collectAdvisories(auditJson?.vulnerabilities);
  const { covered, uncovered } = partitionAdvisories(advisories, allowlist);
  for (const name of trailless) {
    uncovered.push({
      id: null,
      title: 'high/critical finding with no audit trail',
      url: '',
      packages: [name],
    });
  }
  return { covered, uncovered };
}

export function loadAuditJson(
  readStdin = () => readFileSync(0, 'utf8'),
  statStdin = () => fstatSync(0),
) {
  // The TTY check deliberately uses fstat rather than process.stdin: merely
  // touching the stdin stream object switches fd 0 to non-blocking mode,
  // which turns the read below into an EAGAIN race against the slow npm
  // audit writer. fstat leaves the descriptor alone, so the blocking read
  // waits for the full report.
  let isTerminal = true;
  try {
    isTerminal = statStdin().isCharacterDevice();
  } catch {
    isTerminal = true;
  }
  if (isTerminal) {
    throw new GateFailure(2, 'npm-audit-gate expects `npm audit --json` on stdin');
  }
  try {
    return JSON.parse(readStdin());
  } catch {
    throw new GateFailure(2, 'npm audit produced no parseable JSON report');
  }
}

// Pure decision step: everything main() prints and exits on, unit-testable
// without a subprocess.
export function runGate(auditJson, allowlist) {
  const { covered, uncovered } = evaluateAudit(auditJson, allowlist);
  const warnings = covered.map((c) => `npm audit: ${c.id} allowlisted (via ${c.packages.join(', ')})`);
  const errors = uncovered.map(
    (u) => `npm audit: ${u.id ?? 'untraceable finding'} not allowlisted (via ${u.packages.join(', ')})`,
  );
  return { exitCode: uncovered.length > 0 ? 1 : 0, warnings, errors };
}

// Default wiring for production use (real stdin, real console, real exit).
// Tests inject fakes instead. Only these three lines run uncovered.
const defaultLoadReport = () => loadAuditJson(() => readFileSync(0, 'utf8'), () => fstatSync(0));
const defaultReadAllowlist = () => JSON.parse(readFileSync(ALLOWLIST_PATH, 'utf8'));
const defaultQuit = (code) => process.exit(code);

// All side effects hang off `deps` so the whole decision flow — including
// the exit paths — is unit-testable without spawning a subprocess.
export function main(deps = {}) {
  const { loadReport = defaultLoadReport, readAllowlist = defaultReadAllowlist, stdout = console.log, stderr = console.error, quit = defaultQuit } = deps;
  const allowlist = readAllowlist();
  try {
    const { exitCode, warnings, errors } = runGate(loadReport(), allowlist);
    for (const warning of warnings) {
      stdout(`::warning::${warning}`);
    }
    if (exitCode !== 0) {
      for (const error of errors) {
        stderr(`::error::${error}`);
      }
      stderr(`npm audit gate failed: ${errors.length} non-allowlisted high/critical advisories`);
    } else {
      stdout(`npm audit gate passed (${warnings.length} allowlisted high/critical advisories, 0 unlisted)`);
    }
    quit(exitCode);
  } catch (err) {
    if (err instanceof GateFailure) {
      stderr(`::error::${err.message}`);
      quit(err.exitCode);
      return;
    }
    throw err;
  }
}

if (process.argv[1] !== undefined && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
