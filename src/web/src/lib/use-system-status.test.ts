import { describe, expect, it } from 'vitest'

import {
  formatVersion,
  versionSourceLabel,
  versionSourceUrl,
  type SystemStatus,
} from '@/lib/use-system-status'

/** Only the two fields the version label reads. */
const status = (version: string, revision: string | null): SystemStatus => ({
  service: 'dmarc-analyzer-api',
  mode: 'all',
  version,
  revision,
  timestampUtc: '2026-08-10T10:11:28Z',
})

const SHA = 'b1c72c28cbb5c3704a9cddbd86088373df4692a9'

describe('formatVersion', () => {
  it('shows a release as a plain tag', () => {
    // What someone on a pinned tag compares against the releases page. No commit
    // suffix, because there is nothing about this build the tag does not already say.
    expect(formatVersion(status('0.9.0', null))).toBe('v0.9.0')
  })

  it('shows a build past a release with its commit', () => {
    // The case the whole change exists for: on `:latest` or `edge`, "0.9.0" alone
    // would name a release this build is not.
    expect(formatVersion(status('0.9.0', SHA))).toBe('0.9.0+b1c72c2')
  })

  it('leaves a short revision alone', () => {
    expect(formatVersion(status('0.9.0', 'abc'))).toBe('0.9.0+abc')
  })
})

describe('versionSourceUrl', () => {
  it('points a release at its release notes', () => {
    // Reaching the changelog from the version was the second half of the request,
    // so this tag has to be the one GitHub published — v-prefixed.
    expect(versionSourceUrl(status('0.9.0', null))).toBe(
      'https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/releases/tag/v0.9.0',
    )
  })

  it('points a build past a release at the commit, in full', () => {
    // There are no release notes for an unreleased build, and the abbreviation is
    // for reading — the link resolves the exact commit.
    expect(versionSourceUrl(status('0.9.0', SHA))).toBe(
      `https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/commit/${SHA}`,
    )
  })
})

describe('versionSourceLabel', () => {
  it('names release notes only when the link goes to release notes', () => {
    // A reader who gets the label instead of the layout should not be told
    // "release notes" and then be handed a commit.
    expect(versionSourceLabel(status('0.9.0', null))).toBe('Release notes for v0.9.0')
    expect(versionSourceLabel(status('0.9.0', SHA))).toBe(
      'Commit this build was made from, b1c72c2',
    )
  })
})
