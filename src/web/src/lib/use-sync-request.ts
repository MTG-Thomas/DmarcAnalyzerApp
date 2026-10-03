import { useCallback, useEffect, useRef, useState } from 'react'

import { fetchJson } from '@/lib/api'
import type {
  SyncRequestResponse,
  SyncRequestState,
  SyncRequestStatusResponse,
} from '@/lib/entities'

export const SYNC_STATUS_POLL_INTERVAL_MS = 5000
export const SYNC_STATUS_POLL_TIMEOUT_MS = 10 * 60 * 1000

const SYNC_REQUEST_STATES: ReadonlySet<string> = new Set([
  'queued',
  'running',
  'completed',
  'partial',
  'failed',
  'cancelled',
])

const TERMINAL_SYNC_STATUSES: ReadonlySet<SyncRequestState> = new Set([
  'completed',
  'partial',
  'failed',
  'cancelled',
])

export const isSyncRequestState = (value: unknown): value is SyncRequestState =>
  typeof value === 'string' && SYNC_REQUEST_STATES.has(value)

export const isTerminalSyncStatus = (status: SyncRequestState): boolean =>
  TERMINAL_SYNC_STATUSES.has(status)

export type TrackedSyncRequest = {
  sourceId: string
  request: SyncRequestResponse
  status: SyncRequestStatusResponse | null
  polling: boolean
  timedOut: boolean
  pollError: string | null
}

type UseSyncRequestsOptions = {
  onSettled?: (sourceId: string, status: SyncRequestStatusResponse) => void
}

/**
 * Starts manual syncs and tracks each one by polling the `statusUrl` the POST
 * returned. Polling is a chained timeout (never overlapping requests) that
 * stops on a terminal status, after ten minutes, on dismiss, or on unmount.
 * A failed status check records `pollError` and retries; only the deadline
 * or a terminal status ends the chain.
 */
export function useSyncRequests(options: UseSyncRequestsOptions = {}) {
  const { onSettled } = options
  const [requests, setRequests] = useState<Map<string, TrackedSyncRequest>>(() => new Map())
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>())
  // Guards the gaps an `await` leaves open: a dismiss or a re-sync between the
  // fetch resolving and the next state update must not resurrect a dead chain.
  const generations = useRef(new Map<string, number>())
  const mounted = useRef(true)
  const onSettledRef = useRef<UseSyncRequestsOptions['onSettled']>(undefined)

  useEffect(() => {
    onSettledRef.current = onSettled
  }, [onSettled])

  useEffect(() => {
    mounted.current = true
    const pending = timers.current
    return () => {
      mounted.current = false
      for (const timer of pending.values()) clearTimeout(timer)
      pending.clear()
    }
  }, [])

  const stopTimer = useCallback((sourceId: string) => {
    const timer = timers.current.get(sourceId)
    if (timer !== undefined) {
      clearTimeout(timer)
      timers.current.delete(sourceId)
    }
  }, [])

  const pollOnce = useCallback(
    async (
      sourceId: string,
      statusUrl: string,
      deadline: number,
      generation: number,
    ): Promise<void> => {
      if (!mounted.current || generations.current.get(sourceId) !== generation) return

      if (Date.now() >= deadline) {
        setRequests((prev) => {
          const current = prev.get(sourceId)
          if (!current || !current.polling) return prev
          const next = new Map(prev)
          next.set(sourceId, { ...current, polling: false, timedOut: true })
          return next
        })
        return
      }

      try {
        const status = await fetchJson<SyncRequestStatusResponse>(statusUrl)
        if (!mounted.current || generations.current.get(sourceId) !== generation) return
        if (!status || typeof status !== 'object' || !isSyncRequestState(status.status)) {
          throw new Error('Unexpected sync status response')
        }

        if (isTerminalSyncStatus(status.status)) {
          setRequests((prev) => {
            const current = prev.get(sourceId)
            if (!current) return prev
            const next = new Map(prev)
            next.set(sourceId, { ...current, status, polling: false, pollError: null })
            return next
          })
          onSettledRef.current?.(sourceId, status)
          return
        }

        setRequests((prev) => {
          const current = prev.get(sourceId)
          if (!current) return prev
          const next = new Map(prev)
          next.set(sourceId, { ...current, status, pollError: null })
          return next
        })
      } catch (pollError) {
        if (!mounted.current || generations.current.get(sourceId) !== generation) return
        const message = pollError instanceof Error ? pollError.message : 'Status check failed'
        setRequests((prev) => {
          const current = prev.get(sourceId)
          if (!current) return prev
          const next = new Map(prev)
          next.set(sourceId, { ...current, pollError: message })
          return next
        })
      }

      if (!mounted.current || generations.current.get(sourceId) !== generation) return
      stopTimer(sourceId)
      timers.current.set(
        sourceId,
        setTimeout(
          () => void pollOnce(sourceId, statusUrl, deadline, generation),
          SYNC_STATUS_POLL_INTERVAL_MS,
        ),
      )
    },
    [stopTimer],
  )

  const startSync = useCallback(
    async (sourceId: string): Promise<SyncRequestResponse> => {
      const generation = (generations.current.get(sourceId) ?? 0) + 1
      generations.current.set(sourceId, generation)
      stopTimer(sourceId)

      const request = await fetchJson<SyncRequestResponse>(
        `/api/v1/report-sources/${sourceId}/sync`,
        { method: 'POST' },
      )
      if (
        !request ||
        typeof request !== 'object' ||
        typeof request.requestId !== 'string' ||
        typeof request.statusUrl !== 'string'
      ) {
        throw new Error('Unexpected sync response from the server')
      }
      if (!mounted.current || generations.current.get(sourceId) !== generation) return request

      setRequests((prev) => {
        const next = new Map(prev)
        next.set(sourceId, {
          sourceId,
          request,
          status: null,
          polling: true,
          timedOut: false,
          pollError: null,
        })
        return next
      })
      void pollOnce(sourceId, request.statusUrl, Date.now() + SYNC_STATUS_POLL_TIMEOUT_MS, generation)
      return request
    },
    [pollOnce, stopTimer],
  )

  const dismiss = useCallback(
    (sourceId: string) => {
      generations.current.set(sourceId, (generations.current.get(sourceId) ?? 0) + 1)
      stopTimer(sourceId)
      setRequests((prev) => {
        if (!prev.has(sourceId)) return prev
        const next = new Map(prev)
        next.delete(sourceId)
        return next
      })
    },
    [stopTimer],
  )

  return { requests, startSync, dismiss }
}
