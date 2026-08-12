import { useEffect, useRef, useState, type FormEvent } from 'react'

import { Notice } from '@/components/Notice'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Icon } from '@/components/ui/icon'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchJson } from '@/lib/api'
import type { IssuedServiceApiCredential, ServiceApiCredential } from '@/lib/entities'
import { usePageTitle } from '@/lib/use-page-title'

const dateInput = (date: Date) => date.toISOString().slice(0, 10)

const defaultExpiry = () => {
  const date = new Date()
  date.setUTCDate(date.getUTCDate() + 365)
  return dateInput(date)
}

const formatDate = (value: string) => new Date(value).toLocaleDateString()

const statusFor = (credential: ServiceApiCredential) => {
  if (credential.revokedAtUtc) return { label: 'Revoked', variant: 'neutral' as const }
  if (new Date(credential.expiresAtUtc) <= new Date()) {
    return { label: 'Expired', variant: 'warning' as const }
  }
  return { label: 'Active', variant: 'success' as const }
}

export function SettingsPage() {
  usePageTitle('Settings')
  const [credentials, setCredentials] = useState<ServiceApiCredential[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [name, setName] = useState('')
  const [expiresOn, setExpiresOn] = useState(defaultExpiry)
  const [issued, setIssued] = useState<IssuedServiceApiCredential | null>(null)
  const [saving, setSaving] = useState(false)
  const [copied, setCopied] = useState(false)
  const [revokeTarget, setRevokeTarget] = useState<ServiceApiCredential | null>(null)
  const [revoking, setRevoking] = useState(false)
  const revokeRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let cancelled = false
    void fetchJson<ServiceApiCredential[]>('/api/v1/service-credentials')
      .then((result) => {
        if (!cancelled) setCredentials(result)
      })
      .catch((loadError: unknown) => {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load service API keys')
        }
      })
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    if (revokeTarget) revokeRef.current?.focus()
  }, [revokeTarget])

  const closeCreate = () => {
    if (saving) return
    setCreateOpen(false)
    setIssued(null)
    setCopied(false)
    setName('')
    setExpiresOn(defaultExpiry())
  }

  const createCredential = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    setCopied(false)
    try {
      const result = await fetchJson<IssuedServiceApiCredential>('/api/v1/service-credentials', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name: name.trim(),
          expiresAtUtc: new Date(`${expiresOn}T23:59:59Z`).toISOString(),
        }),
      })
      setCredentials((current) => [
        { ...result, revokedAtUtc: null },
        ...(current ?? []),
      ])
      setIssued(result)
    } catch (createError) {
      setError(createError instanceof Error ? createError.message : 'Failed to create service API key')
    } finally {
      setSaving(false)
    }
  }

  const copyToken = async () => {
    if (!issued) return
    setError(null)
    try {
      if (!navigator.clipboard) throw new Error('Clipboard unavailable')
      await navigator.clipboard.writeText(issued.token)
      setCopied(true)
    } catch {
      setError('Could not copy the API key. Select and copy it manually.')
    }
  }

  const revokeCredential = async () => {
    if (!revokeTarget) return
    setRevoking(true)
    setError(null)
    try {
      const result = await fetchJson<ServiceApiCredential>(
        `/api/v1/service-credentials/${revokeTarget.id}`,
        { method: 'DELETE' },
      )
      setCredentials((current) =>
        current?.map((credential) => (credential.id === result.id ? result : credential)) ?? [],
      )
      setRevokeTarget(null)
    } catch (revokeError) {
      setError(revokeError instanceof Error ? revokeError.message : 'Failed to revoke service API key')
    } finally {
      setRevoking(false)
    }
  }

  return (
    <>
      <div className="mb-5">
        <h1 className="text-xl font-semibold tracking-tight text-body">Settings</h1>
        <p className="mt-1 text-sm text-secondary">Account-wide configuration and integrations</p>
      </div>

      {error ? <div className="mb-3.5"><Notice tone="danger">{error}</Notice></div> : null}

      <Card>
        <div className="flex flex-wrap items-start justify-between gap-3 px-5 pt-5">
          <CardHeader
            title="Service API keys"
            description="Global analyst access for trusted integrations such as Bifrost. Report upload keys remain on their report source."
          />
          <Button icon="plus" size="sm" onClick={() => setCreateOpen(true)}>
            Create service API key
          </Button>
        </div>

        {credentials === null && !error ? (
          <div className="flex justify-center py-16">
            <Icon name="loader-circle" size={24} className="animate-spin text-secondary" />
          </div>
        ) : credentials?.length === 0 ? (
          <p className="px-5 pb-6 pt-2 text-sm text-secondary">No service API keys have been created.</p>
        ) : credentials ? (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Prefix</TableHead>
                  <TableHead>Expires</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {credentials.map((credential) => {
                  const status = statusFor(credential)
                  const active = status.label === 'Active'
                  return (
                    <TableRow key={credential.id}>
                      <TableCell className="font-medium text-body">{credential.name}</TableCell>
                      <TableCell className="font-mono text-xs text-body">{credential.prefix}</TableCell>
                      <TableCell className="text-sm text-secondary">{formatDate(credential.expiresAtUtc)}</TableCell>
                      <TableCell><Badge variant={status.variant}>{status.label}</Badge></TableCell>
                      <TableCell className="text-right">
                        {active ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            aria-label={`Revoke ${credential.name}`}
                            onClick={() => setRevokeTarget(credential)}
                          >
                            Revoke
                          </Button>
                        ) : null}
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          </div>
        ) : null}
      </Card>

      {revokeTarget ? (
        <div
          ref={revokeRef}
          role="alertdialog"
          tabIndex={-1}
          aria-labelledby="revoke-service-key-title"
          className="mt-4 focus:outline-none"
        >
          <Notice
            tone="warn"
            title={<span id="revoke-service-key-title">Revoke {revokeTarget.name}?</span>}
          >
            <p>Applications using this key will immediately lose API access.</p>
            <div className="mt-2 flex gap-2">
              <Button variant="danger" size="sm" disabled={revoking} onClick={() => void revokeCredential()}>
                {revoking ? 'Revoking…' : 'Confirm revoke'}
              </Button>
              <Button variant="secondary" size="sm" disabled={revoking} onClick={() => setRevokeTarget(null)}>
                Cancel
              </Button>
            </div>
          </Notice>
        </div>
      ) : null}

      <Dialog open={createOpen} onOpenChange={(open) => (!open ? closeCreate() : setCreateOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create service API key</DialogTitle>
            <DialogDescription>
              This key can access every client as an analyst. It cannot manage users, backups, settings, or credentials.
            </DialogDescription>
          </DialogHeader>

          {issued ? (
            <div className="space-y-4">
              <Notice tone="warn" title="Copy and save this API key now">
                It will not be shown again after you close this dialog.
              </Notice>
              <Input
                aria-label="Service API key"
                mono
                readOnly
                value={issued.token}
                onFocus={(event) => event.currentTarget.select()}
              />
              <div className="flex items-center gap-3">
                <Button type="button" onClick={() => void copyToken()}>Copy API key</Button>
                <Button type="button" variant="secondary" onClick={closeCreate}>Close</Button>
                <span role="status" className="text-sm text-secondary">{copied ? 'API key copied.' : ''}</span>
              </div>
            </div>
          ) : (
            <form onSubmit={createCredential} className="space-y-4">
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-body">Name</span>
                <Input
                  required
                  maxLength={100}
                  value={name}
                  onChange={(event) => setName(event.target.value)}
                  placeholder="Bifrost"
                />
              </label>
              <label className="block space-y-1.5">
                <span className="text-sm font-medium text-body">Expires</span>
                <Input
                  type="date"
                  required
                  min={dateInput(new Date(Date.now() + 86_400_000))}
                  max={defaultExpiry()}
                  value={expiresOn}
                  onChange={(event) => setExpiresOn(event.target.value)}
                />
              </label>
              <div className="flex justify-end gap-2">
                <Button type="button" variant="secondary" disabled={saving} onClick={closeCreate}>Cancel</Button>
                <Button type="submit" disabled={saving || name.trim().length === 0}>
                  {saving ? 'Creating…' : 'Create API key'}
                </Button>
              </div>
            </form>
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}
