import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Icon } from '@/components/ui/icon'
import { Input } from '@/components/ui/input'
import { Notice } from '@/components/Notice'
import { fetchJson } from '@/lib/api'
import type { Client, IssuedMagicLink, MagicLink } from '@/lib/entities'

const formatWhen = (value: string | null) => {
  if (!value) return 'never'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

function linkStatus(link: MagicLink): { label: string; variant: 'success' | 'neutral' | 'warning' } {
  if (link.revokedAtUtc) return { label: 'Revoked', variant: 'neutral' }
  if (new Date(link.expiresAtUtc).getTime() <= Date.now()) return { label: 'Expired', variant: 'warning' }
  return { label: 'Active', variant: 'success' }
}

export function MagicLinksDialog({ client, onClose }: { client: Client; onClose: () => void }) {
  const [links, setLinks] = useState<MagicLink[]>([])
  const [loading, setLoading] = useState(true)
  const [loaded, setLoaded] = useState(false)
  const [action, setAction] = useState<'issue' | 'revoke' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [issued, setIssued] = useState<IssuedMagicLink | null>(null)
  const [copied, setCopied] = useState(false)
  const [label, setLabel] = useState('')
  const [expiresInDays, setExpiresInDays] = useState(7)
  const [revokeTarget, setRevokeTarget] = useState<MagicLink | null>(null)
  const revokeConfirmationRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let cancelled = false

    const loadLinks = async () => {
      try {
        const result = await fetchJson<MagicLink[]>(
          `/api/v1/magic-links?clientId=${encodeURIComponent(client.id)}`,
        )
        if (!cancelled) {
          setLinks(result)
          setLoaded(true)
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load magic links')
        }
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    void loadLinks()
    return () => {
      cancelled = true
    }
  }, [client.id])

  useEffect(() => {
    if (revokeTarget) revokeConfirmationRef.current?.focus()
  }, [revokeTarget])

  const issueLink = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setAction('issue')
    setError(null)
    setCopied(false)
    try {
      const result = await fetchJson<IssuedMagicLink>('/api/v1/magic-links', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ clientId: client.id, expiresInDays, label: label.trim() }),
      })
      setLinks((current) => [
        {
          id: result.id,
          clientId: result.clientId,
          clientName: client.name,
          label: result.label,
          prefix: result.prefix,
          createdAtUtc: result.createdAtUtc,
          expiresAtUtc: result.expiresAtUtc,
          revokedAtUtc: null,
          lastUsedAtUtc: null,
        },
        ...current,
      ])
      setIssued(result)
      setLabel('')
    } catch (issueError) {
      setError(issueError instanceof Error ? issueError.message : 'Failed to create magic link')
    } finally {
      setAction(null)
    }
  }

  const issuedUrl = issued ? `${window.location.origin}${issued.url}` : ''

  const copyIssuedUrl = async () => {
    if (!issued) return
    setError(null)
    try {
      if (!navigator.clipboard) throw new Error('Clipboard access is unavailable')
      await navigator.clipboard.writeText(issuedUrl)
      setCopied(true)
    } catch {
      setError('Could not copy the link. Select and copy it manually.')
    }
  }

  const revokeLink = async () => {
    if (!revokeTarget) return
    setAction('revoke')
    setError(null)
    try {
      const result = await fetchJson<MagicLink>(`/api/v1/magic-links/${revokeTarget.id}/revoke`, {
        method: 'POST',
      })
      setLinks((current) => current.map((link) => (link.id === result.id ? result : link)))
      setIssued((current) => (current?.id === result.id ? null : current))
      setRevokeTarget(null)
    } catch (revokeError) {
      setError(revokeError instanceof Error ? revokeError.message : 'Failed to revoke magic link')
    } finally {
      setAction(null)
    }
  }

  return (
    <Dialog open onOpenChange={(open) => (!open && action === null ? onClose() : undefined)}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Share {client.name}</DialogTitle>
          <DialogDescription>
            Read-only links to this client&apos;s reports. Anyone with a link can view this client only, until
            the link expires or is revoked.
          </DialogDescription>
        </DialogHeader>

        <div className="grid gap-4">
          {error ? <Notice tone="danger">{error}</Notice> : null}

          {issued ? (
            <Notice tone="warn" title="Copy and send this link now">
              <p>It will not be shown again after you close this dialog.</p>
              <Input
                className="mt-2 text-xs"
                aria-label="Magic link"
                readOnly
                mono
                value={issuedUrl}
                onFocus={(event) => event.currentTarget.select()}
              />
              <div className="mt-2 flex items-center gap-2">
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  icon="copy"
                  aria-label="Copy magic link"
                  onClick={() => void copyIssuedUrl()}
                >
                  {copied ? 'Copied' : 'Copy'}
                </Button>
                <span className="text-xs" aria-live="polite">
                  {copied ? 'Link copied.' : ''}
                </span>
              </div>
            </Notice>
          ) : null}

          <form className="grid gap-3 rounded-md border border-border px-3 py-3" onSubmit={issueLink}>
            <div className="text-sm font-semibold text-body">New link</div>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Label
              <Input
                value={label}
                onChange={(e) => setLabel(e.target.value)}
                placeholder="April client review"
                required
                maxLength={100}
              />
            </label>
            <label className="grid gap-1.5 text-sm font-medium text-body">
              Expires after (days)
              <Input
                type="number"
                min={1}
                max={30}
                value={expiresInDays}
                onChange={(e) => setExpiresInDays(Number(e.target.value || 7))}
                required
              />
            </label>
            <div>
              <Button type="submit" size="sm" icon="plus" disabled={action !== null}>
                {action === 'issue' ? 'Creating' : 'Create link'}
              </Button>
            </div>
          </form>

          <div>
            <h3 className="text-sm font-semibold text-body">Issued links</h3>
            <p className="text-xs text-secondary">Prefixes identify links without exposing secrets.</p>
          </div>

          {loading ? (
            <div className="flex justify-center py-8">
              <Icon name="loader-circle" size={20} className="animate-spin text-secondary" />
            </div>
          ) : !loaded ? (
            <p className="rounded-md border border-border px-3 py-6 text-center text-sm text-secondary">
              Magic link metadata is unavailable. Close and reopen this dialog to retry.
            </p>
          ) : links.length === 0 ? (
            <p className="rounded-md border border-border px-3 py-6 text-center text-sm text-secondary">
              No links have been issued for this client.
            </p>
          ) : (
            <div className="space-y-2">
              {links.map((link) => {
                const status = linkStatus(link)
                const confirmationTitleId = `revoke-magic-link-${link.id}`
                return (
                  <div key={link.id} className="space-y-2">
                    <div className="flex flex-col gap-2 rounded-md border border-border px-3 py-2.5 sm:flex-row sm:items-center sm:justify-between">
                      <div className="min-w-0">
                        <p className="truncate text-sm font-semibold text-body">{link.label}</p>
                        <code className="block truncate font-mono text-xs text-secondary">
                          {link.prefix}
                        </code>
                        <p className="mt-0.5 text-xs text-secondary">
                          Expires {formatWhen(link.expiresAtUtc)} · Last used {formatWhen(link.lastUsedAtUtc)}
                        </p>
                      </div>
                      <div className="flex shrink-0 items-center gap-2">
                        <Badge variant={status.variant}>{status.label}</Badge>
                        {link.revokedAtUtc === null ? (
                          <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            aria-label={`Revoke ${link.label}`}
                            disabled={action !== null || revokeTarget !== null}
                            onClick={() => setRevokeTarget(link)}
                          >
                            Revoke
                          </Button>
                        ) : null}
                      </div>
                    </div>
                    {revokeTarget?.id === link.id ? (
                      <div
                        ref={revokeConfirmationRef}
                        role="alertdialog"
                        aria-labelledby={confirmationTitleId}
                        tabIndex={-1}
                        className="focus:outline-hidden"
                      >
                        <Notice tone="danger" title="Revoke this link?">
                          <p id={confirmationTitleId}>
                            Anyone holding the link for {link.label} will lose access immediately.
                          </p>
                          <div className="mt-2 flex gap-2">
                            <Button
                              type="button"
                              size="sm"
                              disabled={action !== null}
                              onClick={() => void revokeLink()}
                            >
                              {action === 'revoke' ? 'Revoking' : 'Revoke link'}
                            </Button>
                            <Button
                              type="button"
                              variant="secondary"
                              size="sm"
                              disabled={action !== null}
                              onClick={() => setRevokeTarget(null)}
                            >
                              Keep
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
        </div>
      </DialogContent>
    </Dialog>
  )
}
