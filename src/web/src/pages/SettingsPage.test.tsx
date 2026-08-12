import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({ fetchJson: vi.fn() }))

import { fetchJson } from '@/lib/api'
import type {
  IssuedServiceApiCredential,
  ServiceApiCredential,
  ServiceApiPermission,
} from '@/lib/entities'
import { SettingsPage } from '@/pages/SettingsPage'

const permissionCatalog: ServiceApiPermission[] = [
  {
    id: 'portfolio.read',
    name: 'Portfolio read access',
    description: 'Read clients, domains, analytics, alerts, and report-source status.',
  },
  {
    id: 'alerts.manage',
    name: 'Alert operations',
    description: 'Acknowledge or close alerts and run evaluation.',
  },
]

const activeCredential: ServiceApiCredential = {
  id: 'service-active',
  name: 'Bifrost',
  prefix: 'abcdefghijklmnopqrstuv',
  permissions: ['portfolio.read', 'alerts.manage'],
  createdAtUtc: '2026-08-12T00:00:00Z',
  expiresAtUtc: '2027-08-12T00:00:00Z',
  revokedAtUtc: null,
}

const issuedCredential: IssuedServiceApiCredential = {
  id: 'service-new',
  name: 'Bifrost',
  prefix: 'newabcdefghijklmnopqrs',
  permissions: ['portfolio.read'],
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
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([activeCredential])
      .mockResolvedValueOnce(permissionCatalog)

    render(<SettingsPage />)

    expect(await screen.findByText('Bifrost')).toBeInTheDocument()
    expect(screen.getByText(activeCredential.prefix)).toBeInTheDocument()
    expect(screen.getByText('Portfolio read access')).toBeInTheDocument()
    expect(screen.getByText('Alert operations')).toBeInTheDocument()
    expect(screen.getByText('Active')).toBeInTheDocument()
    expect(screen.queryByText(/dmarc_api_v1\./)).not.toBeInTheDocument()
  })

  it('creates a bounded key and reveals it once for copy', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(permissionCatalog)
      .mockResolvedValueOnce(issuedCredential)

    render(<SettingsPage />)

    fireEvent.click(await screen.findByRole('button', { name: 'Create service API key' }))
    expect(screen.getByText('No access selected.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Create API key' })).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Bifrost' } })
    fireEvent.click(screen.getByRole('checkbox', { name: /portfolio read access/i }))
    expect(screen.getByText(/1 permission selected: Portfolio read access/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Create API key' }))

    expect(await screen.findByDisplayValue(issuedCredential.token)).toBeInTheDocument()
    expect(screen.getByText(/will not be shown again/i)).toBeInTheDocument()
    expect(fetchJson).toHaveBeenLastCalledWith('/api/v1/service-credentials', expect.objectContaining({
      method: 'POST',
      body: expect.stringContaining('"name":"Bifrost"'),
    }))
    const createRequest = vi.mocked(fetchJson).mock.calls.at(-1)?.[1]
    expect(JSON.parse(String(createRequest?.body))).toMatchObject({
      name: 'Bifrost',
      permissions: ['portfolio.read'],
    })

    fireEvent.click(screen.getByRole('button', { name: 'Copy API key' }))
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(issuedCredential.token))
  })

  it('clears the reveal-once token when the dialog closes', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(permissionCatalog)
      .mockResolvedValueOnce(issuedCredential)

    render(<SettingsPage />)

    fireEvent.click(await screen.findByRole('button', { name: 'Create service API key' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Bifrost' } })
    fireEvent.click(screen.getByRole('checkbox', { name: /portfolio read access/i }))
    fireEvent.click(screen.getByRole('button', { name: 'Create API key' }))
    expect(await screen.findByDisplayValue(issuedCredential.token)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Close' }))
    expect(screen.queryByDisplayValue(issuedCredential.token)).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Create service API key' }))
    expect(screen.queryByDisplayValue(issuedCredential.token)).not.toBeInTheDocument()
  })

  it('keeps the token available for manual copy when the clipboard is unavailable', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(permissionCatalog)
      .mockResolvedValueOnce(issuedCredential)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined })

    render(<SettingsPage />)

    fireEvent.click(await screen.findByRole('button', { name: 'Create service API key' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Bifrost' } })
    fireEvent.click(screen.getByRole('checkbox', { name: /portfolio read access/i }))
    fireEvent.click(screen.getByRole('button', { name: 'Create API key' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Copy API key' }))

    expect(await screen.findByText(/select and copy it manually/i)).toBeInTheDocument()
    expect(screen.getByLabelText('Service API key')).toHaveValue(issuedCredential.token)
  })

  it('requires focused confirmation before revocation', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([activeCredential])
      .mockResolvedValueOnce(permissionCatalog)
      .mockResolvedValueOnce({ ...activeCredential, revokedAtUtc: '2026-08-12T02:00:00Z' })

    render(<SettingsPage />)

    fireEvent.click(await screen.findByRole('button', { name: 'Revoke Bifrost' }))
    expect(screen.getByRole('alertdialog')).toHaveAccessibleDescription(/immediately and permanently/i)
    expect(screen.getByRole('button', { name: 'Confirm revoke' })).toHaveFocus()
    expect(fetchJson).toHaveBeenCalledTimes(2)

    fireEvent.click(screen.getByRole('button', { name: 'Confirm revoke' }))
    await waitFor(() => expect(fetchJson).toHaveBeenCalledWith(
      `/api/v1/service-credentials/${activeCredential.id}`,
      { method: 'DELETE' },
    ))
    expect(await screen.findByText('Revoked')).toBeInTheDocument()
  })
})
