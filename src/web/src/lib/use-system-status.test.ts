import { describe, expect, it } from 'vitest'

import { formatVersion, versionSource, type SystemStatus } from '@/lib/use-system-status'

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

describe('versionSource', () => {
  it('points a release at its release notes, and says so', () => {
    // Reaching the changelog from the version was the second half of the request,
    // so this tag has to be the one GitHub published — v-prefixed. Url and label
    // are asserted together because a link that disagrees with its own label is
    // the failure the pair exists to prevent.
    expect(versionSource(status('0.9.0', null))).toEqual({
      url: 'https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/releases/tag/v0.9.0',
      label: 'Release notes for v0.9.0',
    })
  })

  it('points a build past a release at the commit, in full', () => {
    // There are no release notes for an unreleased build, so the label must not
    // promise them. The abbreviation is for reading; the link resolves the exact
    // commit.
    expect(versionSource(status('0.9.0', SHA))).toEqual({
      url: `https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/commit/${SHA}`,
      label: 'Commit this build was made from, b1c72c2',
    })
  })
})

describe('builds with nothing to link to', () => {
  it('shows a self-built image as text, not a link', () => {
    // The container build's default when no commit is passed — docker-compose.yml
    // and any `docker build .`. It is on no remote, so /commit/local is a 404.
    const built = status('0.9.0', 'local')

    expect(formatVersion(built)).toBe('0.9.0+local')
    expect(versionSource(built)).toBeNull()
  })

  it('does not render an unstamped build as a version', () => {
    // `vunknown`, linking to /releases/tag/vunknown, under a label promising
    // release notes, was the shape of this before the guard.
    const unstamped = status('unknown', null)

    expect(formatVersion(unstamped)).toBe('unknown')
    expect(versionSource(unstamped)).toBeNull()
  })

  it('still links a prerelease, which is a real tag', () => {
    expect(versionSource(status('1.0.0-rc.1', null))?.url).toBe(
      'https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp/releases/tag/v1.0.0-rc.1',
    )
  })

  it('refuses a revision that is not an object name', () => {
    // Anything that is not hex-and-long-enough cannot be resolved, whatever it is.
    expect(versionSource(status('0.9.0', 'not-a-sha'))).toBeNull()
    expect(versionSource(status('0.9.0', 'abc'))).toBeNull()
    // Uppercase is not how git writes an object name, or how the build stamps one,
    // so the shape does not accept it — the regex has no `i` flag by intent.
    expect(versionSource(status('0.9.0', SHA.toUpperCase()))).toBeNull()
  })
})
