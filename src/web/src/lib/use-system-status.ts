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

/**
 * How a build names itself to a self-hoster: `v0.9.0` for a release, and
 * `0.9.0+a1b2c3d` for a build past one.
 *
 * The two read differently on purpose. Someone on a fixed tag gets a number they
 * can match against the releases page, with no suffix to wonder about; someone on
 * `:latest` or `edge` gets the commit, because the release number alone would
 * name a build they are not running. The `v` is only on the exact form, so the
 * prefix itself signals which of the two this is.
 */
export function formatVersion(status: SystemStatus): string {
  return status.revision === null
    ? `v${status.version}`
    : `${status.version}+${status.revision.slice(0, SHORT_REVISION_LENGTH)}`
}

const REPOSITORY_URL = 'https://github.com/dmarc-analyzer-net/DmarcAnalyzerApp'

/**
 * Where the displayed version came from — the release notes for a release, and
 * the commit itself for a build past one, since there are no notes for a build
 * that has not been released.
 *
 * The version being hard to find was only half of what was asked for; the other
 * half was reaching the changelog from it, so the label is a link rather than
 * text a user then has to go and search for.
 */
export function versionSourceUrl(status: SystemStatus): string {
  return status.revision === null
    ? `${REPOSITORY_URL}/releases/tag/v${status.version}`
    : `${REPOSITORY_URL}/commit/${status.revision}`
}

/**
 * What the link goes to, for a reader who gets the label instead of the layout.
 * It has to follow {@link versionSourceUrl} rather than say "release notes" in
 * both cases — on an unreleased build there are none, and the link opens a commit.
 */
export function versionSourceLabel(status: SystemStatus): string {
  return status.revision === null
    ? `Release notes for v${status.version}`
    : `Commit this build was made from, ${status.revision.slice(0, SHORT_REVISION_LENGTH)}`
}
