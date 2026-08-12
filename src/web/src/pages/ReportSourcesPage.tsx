import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'

import { Notice } from '@/components/Notice'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Icon } from '@/components/ui/icon'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { isAdmin } from '@/lib/authz'
import type {
  ApiSourceCredential,
  Client,
  IssuedApiSourceCredential,
  MailboxHealth,
  ReportSource,
  MailboxSyncRun,
  SyncRunStatus,
} from '@/lib/entities'
import { formatRelativeOrDate } from '@/lib/format'
import { usePageTitle } from '@/lib/use-page-title'

type MailboxOpsFilter = 'all' | 'failed' | 'parse-failures' | 'stale-success'

const initialMailboxForm = {
  name: '',
  protocol: 'imap' as 'imap' | 'pop3' | 'api',
  host: '',
  port: 993,
  useTls: true,
  username: '',
  password: '',
  defaultClientId: '',
  isActive: true,
  deleteAfterRetention: false,
}

/** Status pill in the sources table: healthy/running/failing (health-driven). */
const getHealthBadge = (
  status: SyncRunStatus | null | undefined,
): { label: string; variant: 'success' | 'warning' | 'danger' | 'neutral' } => {
  if (status === 'success') return { label: 'Healthy', variant: 'success' }
  if (status === 'running') return { label: 'Running', variant: 'warning' }
  if (status === 'partial') return { label: 'Catching up', variant: 'warning' }
  if (status === 'failed') return { label: 'Failing', variant: 'danger' }
  return { label: 'No data', variant: 'neutral' }
}

/** Raw status pill used in the health + sync-run detail tables. */
const getStatusBadgeVariant = (status: SyncRunStatus | null) => {
  if (status === 'success') return 'success' as const
  if (status === 'failed') return 'danger' as const
  if (status === 'running' || status === 'partial') return 'warning' as const
  return 'neutral' as const
}

const numOrDash = (value: number | null | undefined) =>
  value == null ? '—' : value.toLocaleString('en-US')

const formatWhen = (value: string | null) => {
  if (!value) return 'n/a'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

export function ApiSourceCredentialsDialog({
  source,
  onClose,
}: {
  source: ReportSource
  onClose: () => void
}) {
  const [credentials, setCredentials] = useState<ApiSourceCredential[]>([])
  const [loading, setLoading] = useState(true)
  const [credentialsLoaded, setCredentialsLoaded] = useState(false)
  const [action, setAction] = useState<'issue' | 'revoke' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [issued, setIssued] = useState<IssuedApiSourceCredential | null>(null)
  const [copied, setCopied] = useState(false)
  const [revokeTarget, setRevokeTarget] = useState<ApiSourceCredential | null>(null)
  const revokeConfirmationRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let cancelled = false

    const loadCredentials = async () => {
      try {
        const result = await fetchJson<ApiSourceCredential[]>(
          `/api/v1/report-sources/${source.id}/credentials`,
        )
        if (!cancelled) {
          setCredentials(result)
          setCredentialsLoaded(true)
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load API keys')
        }
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void loadCredentials()
    return () => {
      cancelled = true
    }
  }, [source.id])

  useEffect(() => {
    if (revokeTarget) revokeConfirmationRef.current?.focus()
  }, [revokeTarget])

  const hasActiveCredential = credentials.some((credential) => credential.revokedAtUtc === null)

  const issueCredential = async () => {
    const rotating = hasActiveCredential
    setAction('issue')
    setError(null)
    setCopied(false)
    try {
      const result = await fetchJson<IssuedApiSourceCredential>(
        `/api/v1/report-sources/${source.id}/credentials${rotating ? '/rotate' : ''}`,
        { method: 'POST' },
      )
      setCredentials((current) => [
        {
          id: result.id,
          sourceId: result.sourceId,
          prefix: result.prefix,
          createdAtUtc: result.createdAtUtc,
          revokedAtUtc: null,
        },
        ...current,
      ])
      setIssued(result)
    } catch (issueError) {
      setError(issueError instanceof Error ? issueError.message : 'Failed to create API key')
    } finally {
      setAction(null)
    }
  }

  const copyIssuedCredential = async () => {
    if (!issued) return
    setError(null)
    try {
      if (!navigator.clipboard) throw new Error('Clipboard access is unavailable')
      await navigator.clipboard.writeText(issued.token)
      setCopied(true)
    } catch {
      setError('Could not copy the API key. Select and copy it manually.')
    }
  }

  const revokeCredential = async () => {
    if (!revokeTarget) return
    setAction('revoke')
    setError(null)
    try {
      const result = await fetchJson<ApiSourceCredential>(
        `/api/v1/report-sources/${source.id}/credentials/${revokeTarget.id}`,
        { method: 'DELETE' },
      )
      setCredentials((current) =>
        current.map((credential) => (credential.id === result.id ? result : credential)),
      )
      setIssued((current) => (current?.id === result.id ? null : current))
      setRevokeTarget(null)
    } catch (revokeError) {
      setError(revokeError instanceof Error ? revokeError.message : 'Failed to revoke API key')
    } finally {
      setAction(null)
    }
  }

  return (
    <Dialog open onOpenChange={(open) => (!open && action === null ? onClose() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>API keys for {source.name}</DialogTitle>
          <DialogDescription>
            Keys authenticate report uploads to this source. Secret values are shown only when created.
          </DialogDescription>
        </DialogHeader>

        <div className="grid gap-4">
          {error ? <Notice tone="danger">{error}</Notice> : null}

          {issued ? (
            <Notice tone="warn" title="Copy and save this API key now">
              <p>It will not be shown again after you close this dialog.</p>
              {credentials.some(
                (credential) => credential.id !== issued.id && credential.revokedAtUtc === null,
              ) ? (
                <p className="mt-1">Existing keys remain active until you revoke them.</p>
              ) : null}
              <Input
                className="mt-2 text-xs"
                aria-label="API key"
                readOnly
                mono
                value={issued.token}
                onFocus={(event) => event.currentTarget.select()}
              />
              <div className="mt-2 flex items-center gap-2">
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  icon="copy"
                  aria-label="Copy API key"
                  onClick={() => void copyIssuedCredential()}
                >
                  {copied ? 'Copied' : 'Copy'}
                </Button>
                <span className="text-xs" aria-live="polite">
                  {copied ? 'API key copied.' : ''}
                </span>
              </div>
            </Notice>
          ) : null}

          <div className="flex items-center justify-between gap-3">
            <div>
              <h3 className="text-sm font-semibold text-body">Issued keys</h3>
              <p className="text-xs text-secondary">Prefixes identify keys without exposing secrets.</p>
            </div>
            {!loading && credentialsLoaded ? (
              <Button
                type="button"
                size="sm"
                icon={hasActiveCredential ? 'refresh-cw' : 'plus'}
                disabled={action !== null || revokeTarget !== null}
                onClick={() => void issueCredential()}
              >
                {action === 'issue'
                  ? hasActiveCredential
                    ? 'Rotating'
                    : 'Creating'
                  : hasActiveCredential
                    ? 'Rotate API key'
                    : 'Create API key'}
              </Button>
            ) : null}
          </div>

          {loading ? (
            <div className="flex justify-center py-8">
              <Icon name="loader-circle" size={20} className="animate-spin text-secondary" />
            </div>
          ) : !credentialsLoaded ? (
            <p className="rounded-md border border-border px-3 py-6 text-center text-sm text-secondary">
              API key metadata is unavailable. Close and reopen this dialog to retry.
            </p>
          ) : credentials.length === 0 ? (
            <p className="rounded-md border border-border px-3 py-6 text-center text-sm text-secondary">
              No API keys have been issued for this source.
            </p>
          ) : (
            <div className="space-y-2">
              {credentials.map((credential) => {
                const confirmationTitleId = `revoke-api-key-${credential.id}`
                return (
                  <div key={credential.id} className="space-y-2">
                    <div className="flex flex-col gap-2 rounded-md border border-border px-3 py-2.5 sm:flex-row sm:items-center sm:justify-between">
                      <div className="min-w-0">
                        <code className="block truncate font-mono text-xs text-body">
                          {credential.prefix}
                        </code>
                        <p className="mt-0.5 text-xs text-secondary">
                          Created {formatWhen(credential.createdAtUtc)}
                        </p>
                      </div>
                      <div className="flex shrink-0 items-center gap-2">
                        <Badge variant={credential.revokedAtUtc ? 'neutral' : 'success'}>
                          {credential.revokedAtUtc ? 'Revoked' : 'Active'}
                        </Badge>
                        {credential.revokedAtUtc === null ? (
                          <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            aria-label={`Revoke ${credential.prefix}`}
                            disabled={action !== null || revokeTarget !== null}
                            onClick={() => setRevokeTarget(credential)}
                          >
                            Revoke
                          </Button>
                        ) : null}
                      </div>
                    </div>
                    {revokeTarget?.id === credential.id ? (
                      <div
                        ref={revokeConfirmationRef}
                        role="alertdialog"
                        aria-labelledby={confirmationTitleId}
                        tabIndex={-1}
                        className="focus:outline-none"
                      >
                        <Notice
                          tone="warn"
                          title={
                            <span id={confirmationTitleId}>
                              Revoke API key {revokeTarget.prefix}?
                            </span>
                          }
                        >
                          <p>Applications using this key will stop sending reports immediately.</p>
                          <div className="mt-2 flex justify-end gap-2">
                            <Button
                              type="button"
                              variant="secondary"
                              size="sm"
                              disabled={action !== null}
                              onClick={() => setRevokeTarget(null)}
                            >
                              Cancel
                            </Button>
                            <Button
                              type="button"
                              variant="danger"
                              size="sm"
                              disabled={action !== null}
                              aria-label="Confirm revoke"
                              onClick={() => void revokeCredential()}
                            >
                              {action === 'revoke' ? 'Revoking' : 'Revoke key'}
                            </Button>
                          </div>
                        </Notice>
                      </div>
                    ) : null}
                  </div>
                )
              })}
            </div>
          )}

          <div className="flex justify-end">
            <Button type="button" variant="secondary" disabled={action !== null} onClick={onClose}>
              Close
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}

export function ReportSourcesPage() {
  usePageTitle('Report sources')
  const { user } = useAuth()
  const canManage = isAdmin(user)

  const [clients, setClients] = useState<Client[]>([])
  const [mailboxSources, setReportSources] = useState<ReportSource[]>([])
  const [mailboxHealth, setMailboxHealth] = useState<MailboxHealth[]>([])
  const [syncRuns, setSyncRuns] = useState<MailboxSyncRun[]>([])

  const [search, setSearch] = useState('')
  const [mailboxOpsFilter, setMailboxOpsFilter] = useState<MailboxOpsFilter>('all')
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [syncingId, setSyncingId] = useState<string | null>(null)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingMailboxId, setEditingMailboxId] = useState<string | null>(null)
  const [mailboxForm, setMailboxForm] = useState(initialMailboxForm)
  const [credentialSource, setCredentialSource] = useState<ReportSource | null>(null)

  const loadData = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [clientData, mailboxData] = await Promise.all([
        fetchJson<Client[]>('/api/v1/clients'),
        fetchJson<ReportSource[]>('/api/v1/report-sources'),
      ])

      const [healthData, syncRunData] = await Promise.all([
        fetchJson<MailboxHealth[]>('/api/v1/mailbox-health'),
        fetchJson<MailboxSyncRun[]>('/api/v1/mailbox-sync-runs?limit=40'),
      ])

      setClients(clientData)
      setReportSources(mailboxData)
      setMailboxHealth(healthData)
      setSyncRuns(syncRunData)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load data')
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void loadData()
  }, [loadData])

  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name)),
    [clients],
  )

  const healthBySourceId = useMemo(
    () => new Map(mailboxHealth.map((health) => [health.reportSourceId, health])),
    [mailboxHealth],
  )

  const sourceById = useMemo(
    () => new Map(mailboxSources.map((source) => [source.id, source])),
    [mailboxSources],
  )

  const filteredReportSources = useMemo(() => {
    const q = search.toLowerCase().trim()
    if (!q) return mailboxSources
    return mailboxSources.filter(
      (x) =>
        x.name.toLowerCase().includes(q) ||
        x.host?.toLowerCase().includes(q) ||
        x.username?.toLowerCase().includes(q),
    )
  }, [search, mailboxSources])

  const failingMailboxes = useMemo(
    () => mailboxHealth.filter((health) => health.lastRunStatus === 'failed'),
    [mailboxHealth],
  )

  const healthyCount = useMemo(
    () => mailboxHealth.filter((health) => health.lastRunStatus === 'success').length,
    [mailboxHealth],
  )

  const filteredMailboxHealth = useMemo(() => {
    const now = Date.now()
    const staleThresholdMs = 24 * 60 * 60 * 1000

    return mailboxHealth.filter((health) => {
      if (mailboxOpsFilter === 'failed') {
        return health.lastRunStatus === 'failed'
      }

      if (mailboxOpsFilter === 'parse-failures') {
        return (health.lastRunParseFailures ?? 0) > 0
      }

      if (mailboxOpsFilter === 'stale-success') {
        if (!health.lastSuccessSyncAtUtc) return true
        const lastSuccessMs = new Date(health.lastSuccessSyncAtUtc).getTime()
        if (Number.isNaN(lastSuccessMs)) return true
        return now - lastSuccessMs > staleThresholdMs
      }

      return true
    })
  }, [mailboxHealth, mailboxOpsFilter])

  const filteredReportSourcesForOps = useMemo(() => {
    const ids = new Set(filteredMailboxHealth.map((x) => x.reportSourceId))
    return filteredReportSources.filter((source) => ids.has(source.id))
  }, [filteredReportSources, filteredMailboxHealth])

  const recentSyncRunsByMailbox = useMemo(() => {
    const grouped = new Map<string, MailboxSyncRun[]>()
    for (const run of syncRuns) {
      const current = grouped.get(run.reportSourceId) ?? []
      if (current.length < 3) {
        current.push(run)
        grouped.set(run.reportSourceId, current)
      }
    }

    return grouped
  }, [syncRuns])

  const resetDialog = () => {
    setDialogOpen(false)
    setEditingMailboxId(null)
    setMailboxForm((x) => ({ ...initialMailboxForm, defaultClientId: x.defaultClientId }))
    setError(null)
  }

  const openMailboxDialog = (source?: ReportSource) => {
    setError(null)
    setDialogOpen(true)
    if (source) {
      setEditingMailboxId(source.id)
      setMailboxForm({
        name: source.name,
        protocol: source.protocol,
        host: source.host ?? '',
        port: source.port ?? 993,
        useTls: source.useTls ?? true,
        username: source.username ?? '',
        password: '',
        defaultClientId: source.defaultClientId,
        isActive: source.isActive,
        deleteAfterRetention: source.deleteAfterRetention,
      })
    } else {
      setEditingMailboxId(null)
      setMailboxForm({
        ...initialMailboxForm,
        defaultClientId: sortedClients[0]?.id ?? '',
      })
    }
  }

  const createOrUpdateReportSource = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      const payload = mailboxForm.protocol === 'api'
        ? {
            ...mailboxForm,
            host: null,
            port: null,
            useTls: null,
            username: null,
            password: null,
            deleteAfterRetention: false,
          }
        : { ...mailboxForm }
      if (editingMailboxId && !payload.password) {
        delete (payload as { password?: string }).password
      }

      if (editingMailboxId) {
        await fetchJson(`/api/v1/report-sources/${editingMailboxId}`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        })
      } else {
        await fetchJson('/api/v1/report-sources', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        })
      }

      resetDialog()
      await loadData()
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Failed to save report source')
    }
  }

  const syncNow = async (id: string) => {
    setSyncingId(id)
    setError(null)
    try {
      await fetchJson(`/api/v1/report-sources/${id}/sync`, { method: 'POST' })
      await loadData()
    } catch (syncError) {
      setError(syncError instanceof Error ? syncError.message : 'Failed to sync mailbox')
    } finally {
      setSyncingId(null)
    }
  }

  const lastSyncLabel = (health: MailboxHealth | undefined) => {
    if (health?.lastRunStatus === 'running') return 'Running now'
    return formatRelativeOrDate(health?.lastSuccessSyncAtUtc ?? null)
  }

  const count = mailboxSources.length
  const mailboxCount = mailboxHealth.length
  const subtitle = `${count} ${count === 1 ? 'source' : 'sources'} · ${healthyCount}/${mailboxCount} mailboxes healthy`

  return (
    <>
      <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between sm:gap-4">
        <div>
          <h1 className="font-display text-xl font-bold tracking-tight text-body">Report sources</h1>
          <p className="mt-1 text-sm text-secondary">{subtitle}</p>
        </div>
        <div className="flex flex-wrap items-center gap-2.5 sm:flex-nowrap sm:shrink-0">
          <Input
            icon="search"
            placeholder="Search sources"
            className="w-full sm:w-56"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          {canManage && (
            <Button icon="plus" onClick={() => openMailboxDialog()}>
              Add source
            </Button>
          )}
        </div>
      </div>

      {error ? (
        <div className="mb-3.5 rounded-md border border-[var(--status-danger-bg)] bg-[var(--status-danger-bg)] px-3 py-2 text-sm text-[var(--status-danger-fg)]">
          {error}
        </div>
      ) : null}

      {busy && mailboxSources.length === 0 ? (
        <div className="flex justify-center py-20">
          <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
        </div>
      ) : (
        <>
          <Card pad={false} className="overflow-hidden">
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Mailbox</TableHead>
                    <TableHead>Protocol</TableHead>
                    <TableHead>Host</TableHead>
                    <TableHead>Last sync</TableHead>
                    <TableHead className="text-right">Scanned</TableHead>
                    <TableHead className="text-right">Inserted</TableHead>
                    <TableHead className="text-right">Status</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {filteredReportSources.map((source, index) => {
                    const health = healthBySourceId.get(source.id)
                    const badge = source.protocol === 'api'
                      ? { label: source.isActive ? 'API source' : 'Inactive', variant: 'neutral' as const }
                      : source.isActive
                      ? getHealthBadge(health?.lastRunStatus)
                      : { label: 'Inactive', variant: 'neutral' as const }
                    const isSyncing = syncingId === source.id
                    return (
                      <TableRow key={source.id} last={index === filteredReportSources.length - 1}>
                        <TableCell mono>{source.name}</TableCell>
                        <TableCell mono>
                          {source.protocol === 'api' ? 'api' : `${source.protocol}:${source.port}`}
                        </TableCell>
                        <TableCell mono>{source.host ?? '—'}</TableCell>
                        <TableCell>
                          <span className="text-sm text-secondary">
                            {source.protocol === 'api' ? 'Not applicable' : lastSyncLabel(health)}
                          </span>
                        </TableCell>
                        <TableCell mono align="right">
                          {numOrDash(health?.lastRunMessagesScanned)}
                        </TableCell>
                        <TableCell mono align="right">
                          {numOrDash(health?.lastRunReportsInserted)}
                        </TableCell>
                        <TableCell align="right">
                          <Badge variant={badge.variant} dot>
                            {badge.label}
                          </Badge>
                        </TableCell>
                        <TableCell align="right">
                          <div className="flex justify-end gap-2">
                            {canManage && source.protocol === 'api' ? (
                              <Button
                                variant="secondary"
                                size="sm"
                                icon="key-round"
                                onClick={() => setCredentialSource(source)}
                              >
                                API keys
                              </Button>
                            ) : null}
                            {canManage && (
                              <Button
                                variant="secondary"
                                size="sm"
                                icon="pencil"
                                onClick={() => openMailboxDialog(source)}
                              >
                                Edit
                              </Button>
                            )}
                            {source.protocol !== 'api' ? (
                              <Button
                                variant="secondary"
                                size="sm"
                                disabled={isSyncing}
                                onClick={() => void syncNow(source.id)}
                              >
                                <Icon
                                  name="refresh-cw"
                                  size={14}
                                  className={isSyncing ? 'animate-spin' : undefined}
                                />
                                {isSyncing ? 'Syncing' : 'Sync now'}
                              </Button>
                            ) : null}
                          </div>
                        </TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            </div>
            {filteredReportSources.length === 0 ? (
              <p className="px-5 py-10 text-center text-sm text-secondary">
                No report sources found{search ? ' for the current search' : ''}.
              </p>
            ) : null}
          </Card>

          {failingMailboxes.length > 0 ? (
            <div className="mt-3 space-y-1.5">
              {failingMailboxes.map((health) => {
                const source = sourceById.get(health.reportSourceId)
                return (
                  <div
                    key={health.reportSourceId}
                    className="flex items-center gap-2 text-sm text-secondary"
                  >
                    <span className="inline-flex shrink-0 text-[var(--status-danger-dot)]">
                      <Icon name="circle-alert" size={15} />
                    </span>
                    <span className="font-mono text-xs">{source?.host ?? health.name}</span>
                    <span>failed —</span>
                    <span className="min-w-0 truncate font-mono text-xs" title={health.lastRunError ?? ''}>
                      {health.lastRunError ?? 'unknown error'}
                    </span>
                  </div>
                )
              })}
            </div>
          ) : null}

          <Card pad={false} className="mt-3.5 overflow-hidden">
            <div className="px-5 pt-4 pb-2">
              <CardHeader
                title="Mailbox health"
                description="Operational view of sync outcomes, checkpoints, and latest issues."
                actions={
                  <Select
                    className="w-full sm:w-56"
                    value={mailboxOpsFilter}
                    onChange={(e) => setMailboxOpsFilter(e.target.value as MailboxOpsFilter)}
                  >
                    <option value="all">All mailboxes</option>
                    <option value="failed">Failed mailboxes</option>
                    <option value="parse-failures">Parse failures &gt; 0</option>
                    <option value="stale-success">Stale last success (&gt;24h)</option>
                  </Select>
                }
              />
            </div>
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Mailbox</TableHead>
                    <TableHead>Last status</TableHead>
                    <TableHead>Last success</TableHead>
                    <TableHead>Checkpoint UID</TableHead>
                    <TableHead>Last run metrics</TableHead>
                    <TableHead>Last error</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {filteredMailboxHealth.map((health, index) => (
                    <TableRow
                      key={health.reportSourceId}
                      last={index === filteredMailboxHealth.length - 1}
                    >
                      <TableCell className="font-semibold">{health.name}</TableCell>
                      <TableCell>
                        <Badge variant={getStatusBadgeVariant(health.lastRunStatus)}>
                          {health.lastRunStatus ?? 'unknown'}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        <span className="text-sm text-secondary">
                          {formatWhen(health.lastSuccessSyncAtUtc)}
                        </span>
                      </TableCell>
                      <TableCell mono>{health.lastProcessedUid ?? 'n/a'}</TableCell>
                      <TableCell>
                        <div className="text-xs leading-5 text-secondary">
                          <div>Scanned: {health.lastRunMessagesScanned ?? 0}</div>
                          <div>Attachments: {health.lastRunAttachmentsProcessed ?? 0}</div>
                          <div>Inserted: {health.lastRunReportsInserted ?? 0}</div>
                          <div>Dupes: {health.lastRunReportsSkippedAsDuplicate ?? 0}</div>
                          <div>
                            TLS: {health.lastRunTlsReportsInserted ?? 0} inserted /{' '}
                            {health.lastRunTlsReportsSkippedAsDuplicate ?? 0} dupes
                          </div>
                          <div>Parse failures: {health.lastRunParseFailures ?? 0}</div>
                        </div>
                      </TableCell>
                      <TableCell className="max-w-[420px]">
                        <p
                          className="truncate text-xs text-secondary"
                          title={health.lastRunError ?? ''}
                        >
                          {health.lastRunError ?? 'none'}
                        </p>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
            {filteredMailboxHealth.length === 0 ? (
              <p className="px-5 py-10 text-center text-sm text-secondary">
                No mailboxes match the selected filter.
              </p>
            ) : null}
          </Card>

          <Card pad={false} className="mt-3.5">
            <div className="px-5 pt-4 pb-2">
              <CardHeader title="Recent sync runs" description="Last three runs per report source." />
            </div>
            <CardContent className="space-y-4 pt-2">
              {filteredReportSourcesForOps.length === 0 ? (
                <p className="text-sm text-secondary">No sync runs to show for the selected filter.</p>
              ) : (
                filteredReportSourcesForOps.map((source) => {
                  const runs = recentSyncRunsByMailbox.get(source.id) ?? []
                  return (
                    <div key={source.id} className="rounded-md border border-border p-3">
                      <div className="mb-2 flex items-center justify-between gap-3">
                        <p className="text-sm font-semibold text-body">{source.name}</p>
                        <p className="font-mono text-xs text-secondary">
                          {source.host}:{source.port}
                        </p>
                      </div>
                      {runs.length === 0 ? (
                        <p className="text-xs text-secondary">No sync runs yet.</p>
                      ) : (
                        <div className="overflow-x-auto">
                          <Table>
                            <TableHeader>
                              <TableRow>
                                <TableHead>Status</TableHead>
                                <TableHead>Started</TableHead>
                                <TableHead>Finished</TableHead>
                                <TableHead>Counts</TableHead>
                                <TableHead>Error</TableHead>
                              </TableRow>
                            </TableHeader>
                            <TableBody>
                              {runs.map((run, index) => (
                                <TableRow key={run.id} last={index === runs.length - 1}>
                                  <TableCell>
                                    <Badge variant={getStatusBadgeVariant(run.status)}>
                                      {run.status}
                                    </Badge>
                                  </TableCell>
                                  <TableCell>
                                    <span className="text-sm text-secondary">
                                      {formatWhen(run.startedAtUtc)}
                                    </span>
                                  </TableCell>
                                  <TableCell>
                                    <span className="text-sm text-secondary">
                                      {formatWhen(run.finishedAtUtc)}
                                    </span>
                                  </TableCell>
                                  <TableCell mono>
                                    {run.messagesScanned}/{run.attachmentsProcessed}/
                                    {run.reportsInserted}/{run.reportsSkippedAsDuplicate}/
                                    {run.parseFailures}
                                    {run.tlsReportsInserted > 0 || run.tlsReportsSkippedAsDuplicate > 0
                                      ? ` · tls ${run.tlsReportsInserted}/${run.tlsReportsSkippedAsDuplicate}`
                                      : null}
                                  </TableCell>
                                  <TableCell className="max-w-[260px]">
                                    <p
                                      className="truncate text-xs text-secondary"
                                      title={run.error ?? ''}
                                    >
                                      {run.error ?? 'none'}
                                    </p>
                                  </TableCell>
                                </TableRow>
                              ))}
                            </TableBody>
                          </Table>
                        </div>
                      )}
                    </div>
                  )
                })
              )}
            </CardContent>
          </Card>
        </>
      )}

      <Dialog open={dialogOpen} onOpenChange={(open) => (!open ? resetDialog() : setDialogOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingMailboxId ? 'Edit report source' : 'Add report source'}</DialogTitle>
            <DialogDescription>Configure transport and the authoritative default routing client.</DialogDescription>
          </DialogHeader>
          <form className="grid gap-4" onSubmit={createOrUpdateReportSource}>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Source name
              <Input
                value={mailboxForm.name}
                onChange={(e) => setMailboxForm((x) => ({ ...x, name: e.target.value }))}
                required
              />
            </label>
            <div className="grid grid-cols-2 gap-4">
              <label className="grid gap-1.5 text-sm font-medium text-body">
                Protocol
                <Select
                  value={mailboxForm.protocol}
                  onChange={(e) =>
                    setMailboxForm((x) => ({
                      ...x,
                      protocol: e.target.value as 'imap' | 'pop3' | 'api',
                      deleteAfterRetention: e.target.value === 'api' ? false : x.deleteAfterRetention,
                    }))
                  }
                >
                  <option value="imap">IMAP</option>
                  <option value="pop3">POP3</option>
                  <option value="api">API</option>
                </Select>
              </label>
              {mailboxForm.protocol !== 'api' ? (
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  Port
                  <Input
                    type="number"
                    min={1}
                    mono
                    value={mailboxForm.port}
                    onChange={(e) =>
                      setMailboxForm((x) => ({ ...x, port: Number(e.target.value || 993) }))
                    }
                    required
                  />
                </label>
              ) : <div />}
            </div>
            {mailboxForm.protocol !== 'api' ? (
              <>
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  Host
                  <Input
                    mono
                    value={mailboxForm.host}
                    onChange={(e) => setMailboxForm((x) => ({ ...x, host: e.target.value }))}
                    required
                  />
                </label>
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  Username
                  <Input
                    value={mailboxForm.username}
                    onChange={(e) => setMailboxForm((x) => ({ ...x, username: e.target.value }))}
                    required
                  />
                </label>
                <label className="grid gap-1.5 text-sm font-medium text-body">
                  {editingMailboxId ? 'New password (optional)' : 'Password'}
                  <Input
                    type="password"
                    value={mailboxForm.password}
                    onChange={(e) => setMailboxForm((x) => ({ ...x, password: e.target.value }))}
                    required={!editingMailboxId || sourceById.get(editingMailboxId)?.protocol === 'api'}
                  />
                </label>
              </>
            ) : null}
            {editingMailboxId && sourceById.get(editingMailboxId)?.protocol !== mailboxForm.protocol ? (
              <Notice tone="warn" title="This changes how reports reach the source.">
                {mailboxForm.protocol === 'api'
                  ? 'The saved mailbox connection, checkpoint, last-success state, and mail-retention setting will be cleared.'
                  : 'Enter a complete mailbox connection. API delivery state is not reused as a mailbox checkpoint.'}
              </Notice>
            ) : null}
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Default client
              <Select
                value={mailboxForm.defaultClientId}
                onChange={(e) => setMailboxForm((x) => ({ ...x, defaultClientId: e.target.value }))}
                required
              >
                <option value="">Select default client</option>
                {sortedClients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name}
                  </option>
                ))}
              </Select>
            </label>
            {mailboxForm.protocol !== 'api' ? (
              <label className="flex items-center gap-2 text-sm text-secondary">
                <input
                  type="checkbox"
                  checked={mailboxForm.useTls}
                  onChange={(e) => setMailboxForm((x) => ({ ...x, useTls: e.target.checked }))}
                />
                Use TLS
              </label>
            ) : null}
            <label className="flex items-center gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={mailboxForm.isActive}
                onChange={(e) => setMailboxForm((x) => ({ ...x, isActive: e.target.checked }))}
              />
              Active
            </label>
            {mailboxForm.protocol !== 'api' ? (
              <label className="flex items-center gap-2 text-sm text-secondary">
                <input
                  type="checkbox"
                  checked={mailboxForm.deleteAfterRetention}
                  onChange={(e) =>
                    setMailboxForm((x) => ({ ...x, deleteAfterRetention: e.target.checked }))
                  }
                />
                Delete mail past the retention window
              </label>
            ) : null}
            {mailboxForm.protocol !== 'api' && mailboxForm.deleteAfterRetention ? (
              <Notice tone="warn" title="This deletes mail from the mailbox.">
                Report mail older than the widest retention window of the clients this source
                serves, plus a grace margin, is expunged on a daily pass. It gives the system
                one retention window instead of two — without it, reports purged from the
                database return on the next sync — but the mailbox is also what a report
                replay reads from, so anything deleted is gone unless the object-storage
                archive is on. Deletion is suspended entirely while any client this source
                serves is under legal hold. Check{' '}
                <span className="font-mono text-xs">/api/v1/admin/mailbox-retention/preview</span>{' '}
                to see the cutoff before the pass runs.
              </Notice>
            ) : null}
            <div className="flex justify-end gap-2 pt-1">
              <Button type="button" variant="secondary" onClick={resetDialog}>
                Cancel
              </Button>
              <Button type="submit">{editingMailboxId ? 'Save' : 'Create'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>

      {credentialSource ? (
        <ApiSourceCredentialsDialog
          source={credentialSource}
          onClose={() => setCredentialSource(null)}
        />
      ) : null}
    </>
  )
}
