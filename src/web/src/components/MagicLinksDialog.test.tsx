import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({
  fetchJson: vi.fn(),
}))

import { fetchJson } from '@/lib/api'
import { MagicLinksDialog } from '@/components/MagicLinksDialog'
import type { Client, IssuedMagicLink, MagicLink } from '@/lib/entities'

const client: Client = {
  id: 'client-1',
  name: 'Acme Inc',
  slug: 'acme-inc',
  isActive: true,
  retentionMonths: 27,
  legalHold: false,
  alertsEnabled: true,
  alertComplianceDropPercent: null,
  alertMinMessages: null,
  timezone: 'UTC',
}

const activeLink: MagicLink = {
  id: 'link-active',
  clientId: client.id,
  clientName: client.name,
  label: 'April review',
  prefix: 'abcdefghijklmnopqrstuv',
  createdAtUtc: '2026-04-01T12:00:00Z',
  expiresAtUtc: '2099-04-08T12:00:00Z',
  revokedAtUtc: null,
  lastUsedAtUtc: null,
}

const revokedLink: MagicLink = {
  id: 'link-revoked',
  clientId: client.id,
  clientName: client.name,
  label: 'Old review',
  prefix: 'zyxwvutsrqponmlkjihgfe',
  createdAtUtc: '2026-03-01T12:00:00Z',
  expiresAtUtc: '2099-03-08T12:00:00Z',
  revokedAtUtc: '2026-03-02T12:00:00Z',
  lastUsedAtUtc: '2026-03-01T13:00:00Z',
}

const issuedLink: IssuedMagicLink = {
  id: 'link-new',
  clientId: client.id,
  label: 'May review',
  prefix: 'newabcdefghijklmnopqrs',
  token: 'dmarc_ml_v1.newabcdefghijklmnopqrs.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG',
  url: '/client-view?token=dmarc_ml_v1.newabcdefghijklmnopqrs.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG',
  createdAtUtc: '2026-05-01T12:00:00Z',
  expiresAtUtc: '2099-05-08T12:00:00Z',
}

function renderDialog() {
  return render(<MagicLinksDialog client={client} onClose={vi.fn()} />)
}

describe('magic link sharing', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: vi.fn().mockResolvedValue(undefined) },
    })
  })

  it('lists link metadata without exposing secret material', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce([activeLink, revokedLink])

    renderDialog()

    expect(await screen.findByText(activeLink.label)).toBeInTheDocument()
    expect(screen.getByText(activeLink.prefix)).toBeInTheDocument()
    expect(screen.getByText(revokedLink.label)).toBeInTheDocument()
    expect(screen.getByText('Active')).toBeInTheDocument()
    expect(screen.getByText('Revoked')).toBeInTheDocument()
    expect(screen.queryByText(/dmarc_ml_v1\./)).not.toBeInTheDocument()
    expect(fetchJson).toHaveBeenCalledWith(`/api/v1/magic-links?clientId=${client.id}`)
  })

  it('issues a link with the default 7-day expiry and copies the share URL', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce([]).mockResolvedValueOnce(issuedLink)

    renderDialog()

    fireEvent.change(await screen.findByPlaceholderText('April client review'), {
      target: { value: 'May review' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create link' }))

    const expectedUrl = `${window.location.origin}${issuedLink.url}`
    expect(await screen.findByDisplayValue(expectedUrl)).toBeInTheDocument()
    expect(screen.getByText(/will not be shown again/i)).toBeInTheDocument()
    expect(fetchJson).toHaveBeenCalledWith('/api/v1/magic-links', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ clientId: client.id, expiresInDays: 7, label: 'May review' }),
    })

    fireEvent.click(screen.getByRole('button', { name: 'Copy magic link' }))
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(expectedUrl))
  })

  it('keeps the link visible for manual copy when clipboard access fails', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce([]).mockResolvedValueOnce(issuedLink)
    vi.mocked(navigator.clipboard.writeText).mockRejectedValueOnce(new Error('denied'))

    renderDialog()

    fireEvent.change(await screen.findByPlaceholderText('April client review'), {
      target: { value: 'May review' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create link' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Copy magic link' }))

    expect(await screen.findByText(/select and copy it manually/i)).toBeInTheDocument()
    expect(screen.getByDisplayValue(`${window.location.origin}${issuedLink.url}`)).toBeInTheDocument()
  })

  it('requires confirmation before revoking a link', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([activeLink])
      .mockResolvedValueOnce({ ...activeLink, revokedAtUtc: '2026-04-02T12:00:00Z' })

    renderDialog()

    fireEvent.click(await screen.findByRole('button', { name: `Revoke ${activeLink.label}` }))

    expect(screen.getByRole('alertdialog')).toHaveFocus()
    expect(screen.getByText(/will lose access immediately/i)).toBeInTheDocument()
    expect(fetchJson).not.toHaveBeenCalledWith(
      expect.stringContaining(activeLink.id),
      expect.anything(),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Revoke link' }))

    await waitFor(() =>
      expect(fetchJson).toHaveBeenCalledWith(`/api/v1/magic-links/${activeLink.id}/revoke`, {
        method: 'POST',
      }),
    )
    expect(await screen.findByText('Revoked')).toBeInTheDocument()
  })
})
