import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  evaluateAudit,
  ghsaIdFromUrl,
  loadAuditJson,
  main,
  runGate,
} from './npm-audit-gate.mjs';

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

function charDeviceStat() {
  return { isCharacterDevice: () => true };
}

function pipeStat() {
  return { isCharacterDevice: () => false };
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

describe('loadAuditJson', () => {
  it('parses a piped report', () => {
    const parsed = loadAuditJson(() => '{"a":1}', pipeStat);
    expect(parsed).toEqual({ a: 1 });
  });

  it('refuses a terminal stdin without reading', () => {
    let read = false;
    try {
      loadAuditJson(() => {
        read = true;
        return '';
      }, charDeviceStat);
    } catch (err) {
      expect(err.exitCode).toBe(2);
    }
    expect(read).toBe(false);
  });

  it('fails closed on an unreadable report', () => {
    expect(() => loadAuditJson(() => 'not json', pipeStat)).toThrowError(
      expect.objectContaining({ exitCode: 2 }),
    );
  });

  it('fails closed when stdin cannot be statted', () => {
    let read = false;
    expect(() =>
      loadAuditJson(
        () => {
          read = true;
          return '';
        },
        () => {
          throw new Error('no fd');
        },
      ),
    ).toThrowError(expect.objectContaining({ exitCode: 2 }));
    expect(read).toBe(false);
  });
});

describe('runGate', () => {
  it('passes with warnings on allowlisted advisories', () => {
    const result = runGate(
      audit({ braces: { severity: 'high', via: [ADVISORY] } }),
      ALLOWLIST,
    );
    expect(result.exitCode).toBe(0);
    expect(result.warnings).toHaveLength(1);
    expect(result.errors).toEqual([]);
  });

  it('fails with errors on unlisted advisories', () => {
    const result = runGate(
      audit({ braces: { severity: 'high', via: [ADVISORY] } }),
      {},
    );
    expect(result.exitCode).toBe(1);
    expect(result.errors).toHaveLength(1);
    expect(result.warnings).toEqual([]);
  });
});

describe('main', () => {
  function harness(report, allowlist = ALLOWLIST) {
    const stdout = [];
    const stderr = [];
    const quits = [];
    main({
      loadReport: () => report,
      readAllowlist: () => allowlist,
      stdout: (line) => stdout.push(line),
      stderr: (line) => stderr.push(line),
      quit: (code) => quits.push(code),
    });
    return { stdout, stderr, quits };
  }

  it('prints warnings and quits 0 when all advisories are allowlisted', () => {
    const result = harness(audit({ braces: { severity: 'high', via: [ADVISORY] } }));
    expect(result.quits).toEqual([0]);
    expect(result.stdout.join('\n')).toContain('gate passed');
    expect(result.stderr).toEqual([]);
  });

  it('prints errors and quits 1 on unlisted advisories', () => {
    const result = harness(audit({ braces: { severity: 'high', via: [ADVISORY] } }), {});
    expect(result.quits).toEqual([1]);
    expect(result.stderr.join('\n')).toContain('gate failed');
  });

  it('quits with the loader failure code when the report is unreadable', () => {
    const stdout = [];
    const stderr = [];
    const quits = [];
    main({
      loadReport: () => loadAuditJson(() => 'garbage', pipeStat),
      readAllowlist: () => ALLOWLIST,
      stdout: (line) => stdout.push(line),
      stderr: (line) => stderr.push(line),
      quit: (code) => quits.push(code),
    });
    expect(quits).toEqual([2]);
    expect(stderr.join('\n')).toContain('no parseable JSON');
  });

  it('lets unexpected errors propagate', () => {
    expect(() =>
      main({
        loadReport: () => {
          throw new Error('boom');
        },
        readAllowlist: () => ALLOWLIST,
        stdout: () => {},
        stderr: () => {},
        quit: () => {},
      }),
    ).toThrow('boom');
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
