import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({ fetchJson: vi.fn() }))

import { fetchJson } from '@/lib/api'
import type { IssuedServiceApiCredential, ServiceApiCredential } from '@/lib/entities'
import { SettingsPage } from '@/pages/SettingsPage'

const activeCredential: ServiceApiCredential = {
  id: 'service-active',
  name: 'Bifrost',
  prefix: 'abcdefghijklmnopqrstuv',
  createdAtUtc: '2026-08-12T00:00:00Z',
  expiresAtUtc: '2027-08-12T00:00:00Z',
  revokedAtUtc: null,
}

const issuedCredential: IssuedServiceApiCredential = {
  id: 'service-new',
  name: 'Bifrost',
  prefix: 'newabcdefghijklmnopqrs',
  token: 'dmarc_api_v1.newabcdefghijklmnopqrs.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG',
  createdAtUtc: '2026-08-12T01:00:00Z',
  expiresAtUtc: '2027-08-12T23:59:59Z',
}

describe('service API key settings', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: vi.fn().mockResolvedValue(undefined) },
    })
  })

  it('lists metadata without exposing token material', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce([activeCredential])

    render(<SettingsPage />)

    expect(await screen.findByText('Bifrost')).toBeInTheDocument()
    expect(screen.getByText(activeCredential.prefix)).toBeInTheDocument()
    expect(screen.getByText('Active')).toBeInTheDocument()
    expect(screen.queryByText(/dmarc_api_v1\./)).not.toBeInTheDocument()
  })

  it('creates a bounded key and reveals it once for copy', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(issuedCredential)

    render(<SettingsPage />)

    fireEvent.click(await screen.findByRole('button', { name: 'Create service API key' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Bifrost' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create API key' }))

    expect(await screen.findByDisplayValue(issuedCredential.token)).toBeInTheDocument()
    expect(screen.getByText(/will not be shown again/i)).toBeInTheDocument()
    expect(fetchJson).toHaveBeenLastCalledWith('/api/v1/service-credentials', expect.objectContaining({
      method: 'POST',
      body: expect.stringContaining('"name":"Bifrost"'),
    }))

    fireEvent.click(screen.getByRole('button', { name: 'Copy API key' }))
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(issuedCredential.token))
  })

  it('clears the reveal-once token when the dialog closes', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(issuedCredential)

    render(<SettingsPage />)

    fireEvent.click(await screen.findByRole('button', { name: 'Create service API key' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Bifrost' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create API key' }))
    expect(await screen.findByDisplayValue(issuedCredential.token)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Close' }))
    expect(screen.queryByDisplayValue(issuedCredential.token)).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Create service API key' }))
    expect(screen.queryByDisplayValue(issuedCredential.token)).not.toBeInTheDocument()
  })

  it('requires focused confirmation before revocation', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([activeCredential])
      .mockResolvedValueOnce({ ...activeCredential, revokedAtUtc: '2026-08-12T02:00:00Z' })

    render(<SettingsPage />)

    fireEvent.click(await screen.findByRole('button', { name: 'Revoke Bifrost' }))
    expect(screen.getByRole('alertdialog')).toHaveFocus()
    expect(fetchJson).toHaveBeenCalledTimes(1)

    fireEvent.click(screen.getByRole('button', { name: 'Confirm revoke' }))
    await waitFor(() => expect(fetchJson).toHaveBeenCalledWith(
      `/api/v1/service-credentials/${activeCredential.id}`,
      { method: 'DELETE' },
    ))
    expect(await screen.findByText('Revoked')).toBeInTheDocument()
  })
})
