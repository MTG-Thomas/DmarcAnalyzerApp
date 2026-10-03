import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Icon, type IconName } from '@/components/ui/icon'
import type { SyncRequestState } from '@/lib/entities'
import type { TrackedSyncRequest } from '@/lib/use-sync-request'

type StatusPresentation = {
  label: string
  variant: 'success' | 'warning' | 'danger' | 'neutral'
  icon: IconName
  spin: boolean
}

/**
 * Every state gets its own badge, icon, and label. Queued and cancelled share
 * the neutral badge (neither is running nor an outcome) and stay distinct
 * through the icon and the label.
 */
const STATUS_PRESENTATION: Record<SyncRequestState, StatusPresentation> = {
  queued: { label: 'Queued', variant: 'neutral', icon: 'clock', spin: false },
  running: { label: 'Running', variant: 'warning', icon: 'loader-circle', spin: true },
  completed: { label: 'Completed', variant: 'success', icon: 'check', spin: false },
  partial: { label: 'Partial', variant: 'warning', icon: 'triangle-alert', spin: false },
  failed: { label: 'Failed', variant: 'danger', icon: 'circle-alert', spin: false },
  cancelled: { label: 'Cancelled', variant: 'neutral', icon: 'x', spin: false },
}

/** `messagesScanned` -> `Messages scanned`. Summary keys arrive camelCase. */
const humanizeSummaryKey = (key: string): string => {
  const spaced = key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim()
  if (!spaced) return key
  const lowered = spaced.toLowerCase()
  return lowered.charAt(0).toUpperCase() + lowered.slice(1)
}

type SummaryEntry = { key: string; label: string; value: string }

/** Renders the scalar summary entries; nested objects carry no count to show. */
const summaryEntries = (summary: Record<string, unknown> | null): SummaryEntry[] => {
  if (!summary) return []
  const entries: SummaryEntry[] = []
  for (const [key, value] of Object.entries(summary)) {
    if (typeof value === 'number') {
      entries.push({ key, label: humanizeSummaryKey(key), value: value.toLocaleString('en-US') })
    } else if (typeof value === 'string' || typeof value === 'boolean') {
      entries.push({ key, label: humanizeSummaryKey(key), value: String(value) })
    }
  }
  return entries
}

type SyncRequestPanelProps = {
  entries: TrackedSyncRequest[]
  sourceNames: Map<string, string>
  onDismiss: (sourceId: string) => void
}

/** Live status of the manual syncs started from this page. Renders nothing until the first one. */
export function SyncRequestPanel({ entries, sourceNames, onDismiss }: SyncRequestPanelProps) {
  if (entries.length === 0) return null

  return (
    <Card pad={false} className="mt-3.5">
      <div className="px-5 pt-4 pb-2">
        <CardHeader
          title="Sync status"
          description="Live status of manual syncs started from this page."
        />
      </div>
      <CardContent className="space-y-3 pt-2">
        {entries.map((entry) => {
          const presentation = STATUS_PRESENTATION[entry.status?.status ?? 'queued']
          const counts = summaryEntries(entry.status?.summary ?? null)
          return (
            <div key={entry.sourceId} className="rounded-md border border-border p-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <p className="font-mono text-xs text-body">
                  {sourceNames.get(entry.sourceId) ?? 'Unknown source'}
                </p>
                <Badge variant={presentation.variant}>
                  <Icon
                    name={presentation.icon}
                    size={12}
                    className={presentation.spin ? 'animate-spin' : undefined}
                  />
                  {presentation.label}
                </Badge>
              </div>
              <div className="mt-2 space-y-1.5">
                {entry.polling ? (
                  <p className="text-xs text-secondary">Checking again in 5 seconds.</p>
                ) : null}
                {entry.status && entry.status.attempts > 1 ? (
                  <p className="text-xs text-secondary">Attempts: {entry.status.attempts}</p>
                ) : null}
                {entry.status?.error ? (
                  <p className="text-xs">
                    <span className="text-secondary">Error: </span>
                    <span className="font-mono text-body">{entry.status.error}</span>
                  </p>
                ) : null}
                {counts.length > 0 ? (
                  <div className="grid grid-cols-2 gap-x-4 gap-y-1 pt-1 sm:grid-cols-3">
                    {counts.map((count) => (
                      <div key={count.key} className="flex items-baseline justify-between gap-2 text-xs">
                        <span className="text-secondary">{count.label}</span>
                        <span className="font-mono text-body">{count.value}</span>
                      </div>
                    ))}
                  </div>
                ) : null}
                {entry.pollError && entry.polling ? (
                  <p className="text-xs text-secondary">Latest status check failed, retrying.</p>
                ) : null}
                {entry.timedOut ? (
                  <p className="text-xs text-secondary">
                    Status checks timed out after 10 minutes. Check recent sync runs below for the
                    outcome.
                  </p>
                ) : null}
                {!entry.polling ? (
                  <div className="flex justify-end pt-1">
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => onDismiss(entry.sourceId)}
                    >
                      Dismiss
                    </Button>
                  </div>
                ) : null}
              </div>
            </div>
          )
        })}
      </CardContent>
    </Card>
  )
}
