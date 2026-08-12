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
import { ApiError, fetchJson } from '@/lib/api'
import {
  createPasskey,
  PasskeyBrowserError,
  type PasskeyCredential,
  type PublicKeyCreationOptionsJson,
} from '@/lib/webauthn'

const formatDate = (value: string) => new Date(value).toLocaleDateString()

const isRecentAuthRequired = (error: unknown) =>
  error instanceof ApiError && error.status === 403 && error.message === 'recent authentication required'

const passkeyError = (error: unknown, action: 'add' | 'remove') => {
  if (isRecentAuthRequired(error)) return `Sign out and sign back in, then retry ${action === 'add' ? 'adding' : 'removing'} the passkey.`
  if (error instanceof PasskeyBrowserError) {
    if (error.reason === 'unsupported') return 'Passkeys are not supported by this browser or device.'
    if (error.reason === 'cancelled') return 'Passkey registration was cancelled. Try again when you are ready.'
  }
  return `The passkey could not be ${action === 'add' ? 'added' : 'removed'}. Try again.`
}

export function PasskeySettings() {
  const [enabled, setEnabled] = useState<boolean | null>(null)
  const [passkeys, setPasskeys] = useState<PasskeyCredential[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState<string | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [name, setName] = useState('')
  const [saving, setSaving] = useState(false)
  const [removeTarget, setRemoveTarget] = useState<PasskeyCredential | null>(null)
  const [removing, setRemoving] = useState(false)
  const removeRef = useRef<HTMLDivElement>(null)
  const cardHeaderRef = useRef<HTMLDivElement>(null)
  const removeTriggerRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    let cancelled = false
    void fetchJson<{ passkeys: boolean }>('/api/v1/auth/providers')
      .then(async (providers) => {
        if (!providers.passkeys) return { enabled: false, passkeys: [] as PasskeyCredential[] }
        const result = await fetchJson<{ passkeys: PasskeyCredential[] }>('/api/v1/passkeys')
        return { enabled: true, passkeys: result.passkeys }
      })
      .then((result) => {
        if (!cancelled) {
          setEnabled(result.enabled)
          setPasskeys(result.passkeys)
        }
      })
      .catch(() => {
        if (!cancelled) setError('Passkey settings could not be loaded. Refresh the page to retry.')
      })
    return () => { cancelled = true }
  }, [])

  useEffect(() => {
    if (removeTarget) removeRef.current?.focus()
  }, [removeTarget])

  const add = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    setSuccess(null)
    try {
      const options = await fetchJson<PublicKeyCreationOptionsJson>('/api/v1/passkeys/options', { method: 'POST' })
      const credential = await createPasskey(options)
      const created = await fetchJson<PasskeyCredential>('/api/v1/passkeys', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: name.trim(), credential }),
      })
      setPasskeys((current) => [created, ...(current ?? [])])
      setCreateOpen(false)
      setName('')
      setSuccess(`${created.name} was added.`)
    } catch (addError) {
      setError(passkeyError(addError, 'add'))
    } finally {
      setSaving(false)
    }
  }

  const remove = async () => {
    if (!removeTarget) return
    setRemoving(true)
    setError(null)
    setSuccess(null)
    try {
      await fetchJson(`/api/v1/passkeys/${removeTarget.id}`, { method: 'DELETE' })
      setPasskeys((current) => current?.filter((item) => item.id !== removeTarget.id) ?? [])
      setSuccess(`${removeTarget.name} was removed.`)
      closeRemove(cardHeaderRef.current?.querySelector('button') ?? null)
    } catch (removeError) {
      setError(passkeyError(removeError, 'remove'))
    } finally {
      setRemoving(false)
    }
  }

  const closeCreate = () => {
    if (saving) return
    setCreateOpen(false)
    setName('')
  }

  const openCreate = () => {
    setError(null)
    setSuccess(null)
    setCreateOpen(true)
  }

  const closeRemove = (focusTarget: HTMLButtonElement | null) => {
    setRemoveTarget(null)
    window.setTimeout(() => focusTarget?.focus(), 0)
  }

  return (
    <>
      {error && !createOpen ? <div className="mb-3.5"><Notice tone="danger">{error}</Notice></div> : null}
      {success ? <div className="mb-3.5"><Notice tone="ok">{success}</Notice></div> : null}

      <Card>
        <div ref={cardHeaderRef} className="flex flex-wrap items-start justify-between gap-3 px-5 pt-5">
          <CardHeader
            title="Passkeys"
            description={enabled === false
              ? 'Passkeys are not enabled for this installation.'
              : 'Sign in with this device, a phone, or a security key without relying on single sign-on. Keep another sign-in method for recovery.'}
          />
          {enabled ? <Button icon="plus" size="sm" onClick={openCreate}>Add passkey</Button> : null}
        </div>

        {enabled === false ? null : (enabled === null || passkeys === null) && !error ? (
          <div className="flex justify-center py-16"><Icon name="loader-circle" size={24} className="animate-spin text-secondary" /></div>
        ) : passkeys?.length === 0 ? (
          <p className="px-5 pb-6 pt-2 text-sm text-secondary">No passkeys have been added to your account.</p>
        ) : passkeys ? (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader><TableRow><TableHead>Name</TableHead><TableHead>Added</TableHead><TableHead>Last used</TableHead><TableHead>Backup</TableHead><TableHead className="text-right">Actions</TableHead></TableRow></TableHeader>
              <TableBody>{passkeys.map((passkey) => (
                <TableRow key={passkey.id}>
                  <TableCell className="font-medium text-body">{passkey.name}</TableCell>
                  <TableCell className="text-sm text-secondary">{formatDate(passkey.createdAtUtc)}</TableCell>
                  <TableCell className="text-sm text-secondary">{passkey.lastUsedAtUtc ? formatDate(passkey.lastUsedAtUtc) : 'Never'}</TableCell>
                  <TableCell><Badge variant={passkey.isBackedUp ? 'success' : 'neutral'}>{passkey.isBackedUp ? 'Backed up' : passkey.isBackupEligible ? 'Not backed up' : 'Device only'}</Badge></TableCell>
                  <TableCell className="text-right"><Button variant="ghost" size="sm" aria-label={`Remove ${passkey.name}`} onClick={(event) => { removeTriggerRef.current = event.currentTarget; setRemoveTarget(passkey) }}>Remove</Button></TableCell>
                </TableRow>
              ))}</TableBody>
            </Table>
          </div>
        ) : null}
      </Card>

      {removeTarget ? (
        <div ref={removeRef} role="alertdialog" tabIndex={-1} aria-labelledby="remove-passkey-title" className="mt-4 focus:outline-none">
          <Notice tone="warn" title={<span id="remove-passkey-title">Remove {removeTarget.name}?</span>}>
            <p>This passkey will immediately stop signing in to your account. Other sign-in methods remain unchanged.</p>
            <div className="mt-2 flex gap-2">
              <Button variant="danger" size="sm" disabled={removing} onClick={() => void remove()}>{removing ? 'Removing…' : 'Confirm remove'}</Button>
              <Button variant="secondary" size="sm" disabled={removing} onClick={() => closeRemove(removeTriggerRef.current)}>Cancel</Button>
            </div>
          </Notice>
        </div>
      ) : null}

      <Dialog open={createOpen} onOpenChange={(open) => (!open ? closeCreate() : setCreateOpen(true))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add passkey</DialogTitle>
            <DialogDescription>Your browser will ask where to save the passkey. Registration applies only to your signed-in account.</DialogDescription>
          </DialogHeader>
          {error ? <Notice tone="danger">{error}</Notice> : null}
          <form onSubmit={add} className="space-y-4">
            <label className="block space-y-1.5">
              <span className="text-sm font-medium text-body">Name</span>
              <Input required maxLength={100} value={name} onChange={(event) => setName(event.target.value)} placeholder="Work laptop" />
            </label>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="secondary" disabled={saving} onClick={closeCreate}>Cancel</Button>
              <Button type="submit" disabled={saving || name.trim().length === 0}>{saving ? 'Adding…' : 'Add passkey'}</Button>
            </div>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}
