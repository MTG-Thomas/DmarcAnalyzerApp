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
import type {
  IssuedServiceApiCredential,
  ServiceApiCredential,
  ServiceApiPermission,
} from '@/lib/entities'
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
  const [permissionCatalog, setPermissionCatalog] = useState<ServiceApiPermission[] | null>(null)
  const [permissionCatalogError, setPermissionCatalogError] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [createError, setCreateError] = useState<string | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [name, setName] = useState('')
  const [expiresOn, setExpiresOn] = useState(defaultExpiry)
  const [selectedPermissions, setSelectedPermissions] = useState<string[]>([])
  const [issued, setIssued] = useState<IssuedServiceApiCredential | null>(null)
  const [saving, setSaving] = useState(false)
  const [copied, setCopied] = useState(false)
  const [revokeTarget, setRevokeTarget] = useState<ServiceApiCredential | null>(null)
  const [revoking, setRevoking] = useState(false)
  const [revokeError, setRevokeError] = useState<string | null>(null)
  const revealRef = useRef<HTMLHeadingElement>(null)
  const pageHeadingRef = useRef<HTMLHeadingElement>(null)
  const revokeTriggerRef = useRef<HTMLButtonElement | null>(null)

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
    void fetchJson<ServiceApiPermission[]>('/api/v1/service-credentials/permissions')
      .then((result) => {
        if (!cancelled) setPermissionCatalog(result)
      })
      .catch(() => {
        if (!cancelled) {
          setPermissionCatalog([])
          setPermissionCatalogError(true)
        }
      })
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    if (issued) revealRef.current?.focus()
  }, [issued])

  const closeCreate = () => {
    if (saving) return
    setCreateOpen(false)
    setIssued(null)
    setCopied(false)
    setName('')
    setExpiresOn(defaultExpiry())
    setSelectedPermissions([])
    setCreateError(null)
  }

  const togglePermission = (permissionId: string) => {
    setSelectedPermissions((current) => current.includes(permissionId)
      ? current.filter((id) => id !== permissionId)
      : [...current, permissionId])
  }

  const createCredential = async (event: FormEvent) => {
    event.preventDefault()
    if (selectedPermissions.length === 0) {
      return
    }
    setSaving(true)
    setError(null)
    setCreateError(null)
    setCopied(false)
    try {
      const result = await fetchJson<IssuedServiceApiCredential>('/api/v1/service-credentials', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name: name.trim(),
          expiresAtUtc: new Date(`${expiresOn}T23:59:59Z`).toISOString(),
          permissions: selectedPermissions,
        }),
      })
      const metadata: ServiceApiCredential = {
        id: result.id,
        name: result.name,
        prefix: result.prefix,
        permissions: result.permissions,
        createdAtUtc: result.createdAtUtc,
        expiresAtUtc: result.expiresAtUtc,
        revokedAtUtc: null,
      }
      setCredentials((current) => [metadata, ...(current ?? [])])
      setIssued(result)
    } catch (createError) {
      setCreateError(createError instanceof Error ? createError.message : 'Failed to create service API key')
    } finally {
      setSaving(false)
    }
  }

  const copyToken = async () => {
    if (!issued) return
    setCreateError(null)
    try {
      if (!navigator.clipboard) throw new Error('Clipboard unavailable')
      await navigator.clipboard.writeText(issued.token)
      setCopied(true)
    } catch {
      setCreateError('Could not copy the API key. Select and copy it manually.')
    }
  }

  const revokeCredential = async () => {
    if (!revokeTarget) return
    setRevoking(true)
    setRevokeError(null)
    try {
      const result = await fetchJson<ServiceApiCredential>(
        `/api/v1/service-credentials/${revokeTarget.id}`,
        { method: 'DELETE' },
      )
      setCredentials((current) =>
        current?.map((credential) => (credential.id === result.id ? result : credential)) ?? [],
      )
      setRevokeTarget(null)
      setTimeout(() => pageHeadingRef.current?.focus())
    } catch (revokeError) {
      setRevokeError(revokeError instanceof Error ? revokeError.message : 'Failed to revoke service API key')
    } finally {
      setRevoking(false)
    }
  }

  return (
    <>
      <div className="mb-5">
        <h1 ref={pageHeadingRef} tabIndex={-1} className="text-xl font-semibold tracking-tight text-body focus-visible:shadow-[var(--focus-ring)] focus:outline-none">Settings</h1>
        <p className="mt-1 text-sm text-secondary">Account-wide configuration and integrations</p>
      </div>

      {error ? <div className="mb-3.5"><Notice tone="danger">{error}</Notice></div> : null}
      {permissionCatalogError ? (
        <div className="mb-3.5">
          <Notice tone="danger">Service API key permissions could not be loaded. Existing keys remain available, but new keys cannot be created.</Notice>
        </div>
      ) : null}

      <Card>
        <div className="flex flex-wrap items-start justify-between gap-3 px-5 pt-5">
          <CardHeader
            title="Service API keys"
            description="Selectable account-wide access for trusted integrations such as Bifrost. Report upload keys remain on their report source."
          />
          <Button icon="plus" size="sm" disabled={!permissionCatalog?.length} onClick={() => setCreateOpen(true)}>
            Create service API key
          </Button>
        </div>

        {credentials === null && !error ? (
          <div role="status" className="flex justify-center py-16">
            <span className="sr-only">Loading service API keys</span>
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
                      <TableCell className="min-w-56">
                        <span className="font-medium text-body">{credential.name}</span>
                        <ul className="mt-1 space-y-0.5 text-xs text-secondary" aria-label={`${credential.name} permissions`}>
                          {credential.permissions.map((permissionId) => (
                            <li key={permissionId}>
                              {permissionCatalog?.find((permission) => permission.id === permissionId)?.name ?? permissionId}
                            </li>
                          ))}
                        </ul>
                      </TableCell>
                      <TableCell className="font-mono text-xs text-body">{credential.prefix}</TableCell>
                      <TableCell className="text-sm text-secondary">{formatDate(credential.expiresAtUtc)}</TableCell>
                      <TableCell><Badge variant={status.variant}>{status.label}</Badge></TableCell>
                      <TableCell className="text-right">
                        {active ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            aria-label={`Revoke ${credential.name}`}
                            onClick={(event) => {
                              revokeTriggerRef.current = event.currentTarget
                              setRevokeError(null)
                              setRevokeTarget(credential)
                            }}
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

      <Dialog
        open={revokeTarget !== null}
        onOpenChange={(open) => {
          if (!open && !revoking) {
            setRevokeError(null)
            setRevokeTarget(null)
          }
        }}
      >
        <DialogContent
          role="alertdialog"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            const target = revokeTriggerRef.current
            if (target?.isConnected) target.focus()
            else pageHeadingRef.current?.focus()
          }}
        >
          <DialogHeader>
            <DialogTitle>Revoke {revokeTarget?.name}?</DialogTitle>
            <DialogDescription>
              Applications using this key will immediately and permanently lose its granted access. Create and deploy a replacement key before revoking this one if access must continue.
            </DialogDescription>
          </DialogHeader>
          {revokeTarget ? (
            <>
              <p className="mb-4 text-sm text-body">
                Granted access: {revokeTarget.permissions
                  .map((permissionId) => permissionCatalog?.find((permission) => permission.id === permissionId)?.name ?? permissionId)
                  .join(', ')}.
              </p>
              {revokeError ? <div className="mb-4"><Notice tone="danger">{revokeError}</Notice></div> : null}
              <div className="flex flex-wrap justify-end gap-2">
                <Button variant="secondary" disabled={revoking} onClick={() => {
                  setRevokeError(null)
                  setRevokeTarget(null)
                }}>
                  Cancel
                </Button>
                <Button autoFocus variant="danger" disabled={revoking} onClick={() => void revokeCredential()}>
                  {revoking ? 'Revoking…' : 'Confirm revoke'}
                </Button>
              </div>
            </>
          ) : null}
        </DialogContent>
      </Dialog>

      <Dialog open={createOpen} onOpenChange={(open) => (!open ? closeCreate() : setCreateOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create service API key</DialogTitle>
            <DialogDescription>
              Choose only the access this integration needs. Service keys cannot manage users, backups, retention, authentication, or credentials.
            </DialogDescription>
          </DialogHeader>

          {issued ? (
            <div className="space-y-4">
              <h3 ref={revealRef} tabIndex={-1} className="font-display text-base font-semibold text-body focus:outline-none focus-visible:shadow-[var(--focus-ring)]">
                API key created
              </h3>
              <p role="status" className="text-sm text-secondary">{issued.name} is ready to configure.</p>
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
              {createError ? <Notice tone="danger">{createError}</Notice> : null}
              <div>
                <p className="text-sm font-medium text-body">Granted access</p>
                <ul className="mt-1 list-disc space-y-1 pl-5 text-sm text-secondary">
                  {issued.permissions.map((permissionId) => (
                    <li key={permissionId}>
                      {permissionCatalog?.find((permission) => permission.id === permissionId)?.name ?? permissionId}
                    </li>
                  ))}
                </ul>
                <p className="mt-2 text-sm text-secondary">
                  Permissions cannot be changed after creation; create a replacement key and revoke this one.
                </p>
              </div>
              <div className="flex flex-wrap items-center gap-3">
                <Button type="button" onClick={() => void copyToken()}>Copy API key</Button>
                <Button type="button" variant="secondary" onClick={closeCreate}>Close</Button>
                <span role="status" className="basis-full text-sm text-secondary sm:basis-auto">{copied ? 'API key copied.' : ''}</span>
              </div>
            </div>
          ) : (
            <form onSubmit={createCredential} className="space-y-4">
              {createError ? <Notice tone="danger">{createError}</Notice> : null}
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
              <fieldset aria-describedby="service-permissions-summary">
                <legend className="text-sm font-medium text-body">Permissions</legend>
                <div className="mt-2 space-y-2">
                  {permissionCatalog?.map((permission) => (
                    <label
                      key={permission.id}
                      className="flex min-h-11 cursor-pointer items-start gap-3 rounded-lg border border-border px-3 py-2.5 hover:bg-sunken"
                    >
                      <input
                        type="checkbox"
                        className="mt-0.5 size-4 shrink-0 accent-brand focus-visible:shadow-[var(--focus-ring)]"
                        checked={selectedPermissions.includes(permission.id)}
                        onChange={() => togglePermission(permission.id)}
                      />
                      <span>
                        <span className="block text-sm font-medium text-body">{permission.name}</span>
                        <span className="block text-xs text-secondary">{permission.description}</span>
                      </span>
                    </label>
                  ))}
                </div>
              </fieldset>
              <div id="service-permissions-summary" className="rounded-lg bg-sunken px-3 py-2.5 text-sm text-body">
                {selectedPermissions.length === 0
                  ? 'No access selected.'
                  : `${selectedPermissions.length} permission${selectedPermissions.length === 1 ? '' : 's'} selected: ${permissionCatalog
                    ?.filter((permission) => selectedPermissions.includes(permission.id))
                    .map((permission) => permission.name)
                    .join(', ')}.`}
              </div>
              <div className="flex justify-end gap-2">
                <Button type="button" variant="secondary" disabled={saving} onClick={closeCreate}>Cancel</Button>
                <Button type="submit" disabled={saving || name.trim().length === 0 || selectedPermissions.length === 0}>
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
