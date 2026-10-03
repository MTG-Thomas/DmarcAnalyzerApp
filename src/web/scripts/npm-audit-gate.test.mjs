import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { evaluateAudit, ghsaIdFromUrl } from './npm-audit-gate.mjs';

const ALLOWLIST = {
  'ghsa-vfj7-8cjw-p6xm': { reason: 'test entry' },
};

const ADVISORY = {
  name: 'braces',
  url: 'https://github.com/advisories/GHSA-vfj7-8cjw-p6xm',
  severity: 'high',
  title: 'test advisory',
};

function audit(entries) {
  return { vulnerabilities: entries };
}

describe('ghsaIdFromUrl', () => {
  it('extracts the advisory id in canonical lowercase', () => {
    expect(ghsaIdFromUrl(ADVISORY.url)).toBe('ghsa-vfj7-8cjw-p6xm');
  });

  it('returns null for missing or non-advisory urls', () => {
    expect(ghsaIdFromUrl(null)).toBeNull();
    expect(ghsaIdFromUrl('https://example.com/nope')).toBeNull();
  });
});

describe('evaluateAudit', () => {
  it('covers an allowlisted advisory across the whole chain', () => {
    const { covered, uncovered } = evaluateAudit(
      audit({
        braces: { severity: 'high', via: [ADVISORY, 'micromatch'] },
        chokidar: { severity: 'high', via: ['braces'] },
        tailwindcss: { severity: 'high', via: ['chokidar', 'micromatch'] },
      }),
      ALLOWLIST,
    );
    expect(uncovered).toEqual([]);
    expect(covered).toHaveLength(1);
    expect(covered[0].id).toBe('ghsa-vfj7-8cjw-p6xm');
  });

  it('fails an unknown advisory', () => {
    const other = { ...ADVISORY, url: 'https://github.com/advisories/GHSA-aaaa-bbbb-cccc' };
    const { uncovered } = evaluateAudit(
      audit({ evil: { severity: 'high', via: [other] } }),
      ALLOWLIST,
    );
    expect(uncovered).toHaveLength(1);
    expect(uncovered[0].id).toBe('ghsa-aaaa-bbbb-cccc');
  });

  it('fails a high finding with an empty audit trail', () => {
    const { uncovered } = evaluateAudit(
      audit({ mystery: { severity: 'high', via: [] } }),
      ALLOWLIST,
    );
    expect(uncovered).toHaveLength(1);
  });

  it('ignores moderate and low findings', () => {
    const { covered, uncovered } = evaluateAudit(
      audit({
        a: { severity: 'moderate', via: [ADVISORY] },
        b: { severity: 'low', via: [] },
      }),
      ALLOWLIST,
    );
    expect(covered).toEqual([]);
    expect(uncovered).toEqual([]);
  });

  it('fails critical findings too', () => {
    const { uncovered } = evaluateAudit(
      audit({ evil: { severity: 'critical', via: [ADVISORY] } }),
      {},
    );
    expect(uncovered).toHaveLength(1);
  });
});

describe('cli', () => {
  // import.meta.url is virtualized under vitest, so resolve from the package
  // root (vitest runs with cwd set there).
  const SCRIPT = path.resolve(process.cwd(), 'scripts/npm-audit-gate.mjs');

  function runCli(stdin) {
    try {
      const stdout = execFileSync(process.execPath, [SCRIPT], {
        input: stdin,
        encoding: 'utf8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      return { exit: 0, stdout };
    } catch (err) {
      return { exit: err.status, stdout: err.stdout, stderr: err.stderr };
    }
  }

  it('exits 0 when every high advisory is allowlisted', () => {
    const result = runCli(JSON.stringify(audit({
      braces: { severity: 'high', via: [ADVISORY, 'micromatch'] },
    })));
    expect(result.exit).toBe(0);
    expect(result.stdout).toContain('gate passed');
  });

  it('exits 1 on a non-allowlisted advisory', () => {
    const other = { ...ADVISORY, url: 'https://github.com/advisories/GHSA-aaaa-bbbb-cccc' };
    const result = runCli(JSON.stringify(audit({
      evil: { severity: 'high', via: [other] },
    })));
    expect(result.exit).toBe(1);
    expect(result.stderr).toContain('ghsa-aaaa-bbbb-cccc');
  });

  it('exits 2 when stdin is not a report', () => {
    const result = runCli('this is not json');
    expect(result.exit).toBe(2);
  });

  it('exits 2 without reading when stdin is a terminal device', () => {
    // Ignored stdio lands on /dev/null, a character device like a terminal:
    // the gate must refuse immediately instead of blocking on input.
    let exit = null;
    try {
      execFileSync(process.execPath, [SCRIPT], { stdio: ['ignore', 'pipe', 'pipe'] });
    } catch (err) {
      exit = err.status;
    }
    expect(exit).toBe(2);
  });
});
