import { useEffect, useState } from 'react'

import { fetchJson } from '@/lib/api'

export interface SystemStatus {
  service: string
  /** The resolved `APP_MODE` — `api`, `worker`, `all`, `migrate` or `mta-sts`. */
  mode: string
  /** The release this build came from, e.g. `0.9.0`. */
  version: string
  /**
   * The full commit SHA, or null on a release build. Present exactly when the
   * build was not cut from a tag, which is what distinguishes an `edge` image
   * from the release it was built past.
   */
  revision: string | null
  timestampUtc: string
}

let statusPromise: Promise<SystemStatus | null> | null = null

/**
 * Fetched once per app load and shared, because this never changes while the
 * page is open — the process would have to restart, and that ends the session.
 *
 * Failure resolves to null rather than rejecting or reporting a message: the
 * only consumer is a version label, and an app that cannot reach its own API has
 * louder problems to show the user than that.
 */
function loadSystemStatus(): Promise<SystemStatus | null> {
  statusPromise ??= fetchJson<SystemStatus>('/api/v1/system/status').catch(() => null)
  return statusPromise
}

export function useSystemStatus(): SystemStatus | null {
  const [status, setStatus] = useState<SystemStatus | null>(null)

  useEffect(() => {
    let cancelled = false
    void loadSystemStatus().then((value) => {
      if (!cancelled) setStatus(value)
    })
    return () => {
      cancelled = true
    }
  }, [])

  return status
}

/** Commit characters shown to a human. Git's own abbreviation length. */
const SHORT_REVISION_LENGTH = 7

/** A release number, optionally with a prerelease label: `0.9.0`, `1.0.0-rc.1`. */
const VERSION_SHAPE = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/

/**
 * A git object name, abbreviated or full. Lowercase only, no `i` flag: that is how
 * git writes an object name and how the build stamps one, so accepting uppercase
 * would widen the shape past what it is meant to describe rather than describe it.
 */
const REVISION_SHAPE = /^[0-9a-f]{7,40}$/

/**
 * Whether the reported build is one GitHub can be asked about. False for the two
 * shapes that reach here legitimately and resolve to nothing: `unknown`, when the
 * build stamped no version at all, and `local`, the container build's default when
 * no commit was passed — an image someone built themselves, which is on no remote.
 *
 * Guarding on shape rather than on a flag because both values arrive through the
 * same field as a real one, and a link built from either is a 404 with a label
 * promising release notes.
 */
function hasResolvableSource(status: SystemStatus): boolean {
  return (
    VERSION_SHAPE.test(status.version) &&
    (status.revision === null || REVISION_SHAPE.test(status.revision))
  )
}

/**
 * How a build names itself to a self-hoster: `v0.9.0` for a release,
 * `0.9.0+a1b2c3d` for a build past one, and `0.9.0+local` for one somebody built
 * themselves.
 *
 * The first two read differently on purpose. Someone on a fixed tag gets a number
 * they can match against the releases page, with no suffix to wonder about;
 * someone on `:latest` or `edge` gets the commit, because the release number alone
 * would name a build they are not running. The `v` is only on the exact form, so
 * the prefix itself signals which of the two this is — and a build with any
 * revision at all, resolvable or not, never gets that prefix.
 */
export function formatVersion(status: SystemStatus): string {
  if (!VERSION_SHAPE.test(status.version)) {
    // `unknown`. Shown as-is rather than dressed up as `vunknown`.
    return status.version
  }

  return status.revision === null
    ? `v${status.version}`
    : `${status.version}+${status.revision.slice(0, SHORT_REVISION_LENGTH)}`
}

const REPOSITORY_URL = 'https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp'

export interface VersionSource {
  url: string
  /**
   * What the link goes to, for a reader who gets the label instead of the layout.
   * Has to follow the url rather than say "release notes" in both cases — on an
   * unreleased build there are none, and the link opens a commit.
   */
  label: string
}

/**
 * Where the displayed version came from — the release notes for a release, and
 * the commit itself for a build past one, since there are no notes for a build
 * that has not been released. Null when there is nothing to link to, which the
 * caller renders as plain text.
 *
 * The version being hard to find was only half of what was asked for; the other
 * half was reaching the changelog from it, so the label is a link rather than
 * text a user then has to go and search for.
 *
 * Url and label together, rather than a function each: they answer the same
 * question and are wrong if they ever disagree, so a caller should not be able to
 * pair a commit link with a promise of release notes, and should not have to
 * null-check the same shape twice to use them.
 */
export function versionSource(status: SystemStatus): VersionSource | null {
  if (!hasResolvableSource(status)) {
    return null
  }

  return status.revision === null
    ? {
        url: `${REPOSITORY_URL}/releases/tag/v${status.version}`,
        label: `Release notes for v${status.version}`,
      }
    : {
        url: `${REPOSITORY_URL}/commit/${status.revision}`,
        label: `Commit this build was made from, ${status.revision.slice(0, SHORT_REVISION_LENGTH)}`,
      }
}
