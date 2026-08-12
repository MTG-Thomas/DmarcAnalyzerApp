import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({
  fetchJson: vi.fn(),
}))

import { fetchJson } from '@/lib/api'
import type { ApiSourceCredential, IssuedApiSourceCredential, ReportSource } from '@/lib/entities'
import { ApiSourceCredentialsDialog } from '@/pages/ReportSourcesPage'

const source: ReportSource = {
  id: 'source-api',
  name: 'Report uploader',
  protocol: 'api',
  host: null,
  port: null,
  useTls: null,
  username: null,
  defaultClientId: 'client-1',
  defaultClientName: 'Example client',
  isActive: true,
  deleteAfterRetention: false,
  oldestMessageAtUtc: null,
}

const activeCredential: ApiSourceCredential = {
  id: 'credential-active',
  sourceId: source.id,
  prefix: 'abcdefghijklmnopqrstuv',
  createdAtUtc: '2026-08-11T20:00:00Z',
  revokedAtUtc: null,
}

const revokedCredential: ApiSourceCredential = {
  id: 'credential-revoked',
  sourceId: source.id,
  prefix: 'zyxwvutsrqponmlkjihgfe',
  createdAtUtc: '2026-08-10T20:00:00Z',
  revokedAtUtc: '2026-08-11T19:00:00Z',
}

const issuedCredential: IssuedApiSourceCredential = {
  id: 'credential-new',
  sourceId: source.id,
  prefix: 'newabcdefghijklmnopqrs',
  token: 'dmarc_v1.newabcdefghijklmnopqrs.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG',
  createdAtUtc: '2026-08-11T21:00:00Z',
}

function renderDialog() {
  return render(<ApiSourceCredentialsDialog source={source} onClose={vi.fn()} />)
}

function CredentialDialogHarness() {
  const [open, setOpen] = useState(true)
  return open ? (
    <ApiSourceCredentialsDialog source={source} onClose={() => setOpen(false)} />
  ) : (
    <button type="button" onClick={() => setOpen(true)}>
      Reopen
    </button>
  )
}

describe('API source credential lifecycle', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: vi.fn().mockResolvedValue(undefined) },
    })
  })

  it('lists credential metadata without exposing secret material', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce([activeCredential, revokedCredential])

    renderDialog()

    expect(await screen.findByText(activeCredential.prefix)).toBeInTheDocument()
    expect(screen.getByText(revokedCredential.prefix)).toBeInTheDocument()
    expect(screen.getByText('Active')).toBeInTheDocument()
    expect(screen.getByText('Revoked')).toBeInTheDocument()
    expect(screen.queryByText(/dmarc_v1\./)).not.toBeInTheDocument()
  })

  it('does not offer issuance when credential metadata cannot be loaded', async () => {
    vi.mocked(fetchJson).mockRejectedValueOnce(new Error('metadata unavailable'))

    renderDialog()

    expect(await screen.findByText('metadata unavailable')).toBeInTheDocument()
    expect(screen.getByText(/API key metadata is unavailable/i)).toBeInTheDocument()
    expect(screen.queryByText(/No API keys have been issued/i)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Create API key' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Rotate API key' })).not.toBeInTheDocument()
  })

  it('issues the first key, warns that it is reveal-once, and copies it', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(issuedCredential)

    renderDialog()

    fireEvent.click(await screen.findByRole('button', { name: 'Create API key' }))

    expect(await screen.findByDisplayValue(issuedCredential.token)).toBeInTheDocument()
    expect(screen.getByText(/will not be shown again/i)).toBeInTheDocument()
    expect(fetchJson).toHaveBeenCalledWith(`/api/v1/report-sources/${source.id}/credentials`, {
      method: 'POST',
    })

    fireEvent.click(screen.getByRole('button', { name: 'Copy API key' }))
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(issuedCredential.token))
  })

  it('clears the reveal-once token when the dialog closes', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(issuedCredential)
      .mockResolvedValueOnce([
        {
          ...issuedCredential,
          token: undefined,
          revokedAtUtc: null,
        },
      ])

    render(<CredentialDialogHarness />)

    fireEvent.click(await screen.findByRole('button', { name: 'Create API key' }))
    expect(await screen.findByDisplayValue(issuedCredential.token)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Close' }))
    expect(screen.queryByDisplayValue(issuedCredential.token)).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Reopen' }))
    expect(await screen.findByText(issuedCredential.prefix)).toBeInTheDocument()
    expect(screen.queryByDisplayValue(issuedCredential.token)).not.toBeInTheDocument()
  })

  it('keeps the token visible for manual copy when clipboard access fails', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce(issuedCredential)
    vi.mocked(navigator.clipboard.writeText).mockRejectedValueOnce(new Error('denied'))

    renderDialog()

    fireEvent.click(await screen.findByRole('button', { name: 'Create API key' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Copy API key' }))

    expect(await screen.findByText(/select and copy it manually/i)).toBeInTheDocument()
    expect(screen.getByDisplayValue(issuedCredential.token)).toBeInTheDocument()
    const tokenField = screen.getByRole('textbox', { name: 'API key' }) as HTMLInputElement
    tokenField.focus()
    expect(tokenField).toHaveFocus()
    expect(tokenField.selectionStart).toBe(0)
    expect(tokenField.selectionEnd).toBe(issuedCredential.token.length)
  })

  it('rotates by adding an overlapping key without revoking the active key', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([activeCredential])
      .mockResolvedValueOnce(issuedCredential)

    renderDialog()

    fireEvent.click(await screen.findByRole('button', { name: 'Rotate API key' }))

    expect(await screen.findByDisplayValue(issuedCredential.token)).toBeInTheDocument()
    expect(screen.getByText(/existing keys remain active/i)).toBeInTheDocument()
    expect(fetchJson).toHaveBeenCalledWith(
      `/api/v1/report-sources/${source.id}/credentials/rotate`,
      { method: 'POST' },
    )
    expect(fetchJson).not.toHaveBeenCalledWith(
      expect.stringContaining(activeCredential.id),
      expect.objectContaining({ method: 'DELETE' }),
    )
  })

  it('requires confirmation before revoking a key', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce([activeCredential])
      .mockResolvedValueOnce({ ...activeCredential, revokedAtUtc: '2026-08-11T22:00:00Z' })

    renderDialog()

    fireEvent.click(await screen.findByRole('button', { name: `Revoke ${activeCredential.prefix}` }))

    expect(screen.getByRole('alertdialog')).toHaveFocus()
    expect(screen.getByText(/applications using this key will stop/i)).toBeInTheDocument()
    expect(fetchJson).not.toHaveBeenCalledWith(
      expect.stringContaining(activeCredential.id),
      expect.objectContaining({ method: 'DELETE' }),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Confirm revoke' }))

    await waitFor(() =>
      expect(fetchJson).toHaveBeenCalledWith(
        `/api/v1/report-sources/${source.id}/credentials/${activeCredential.id}`,
        { method: 'DELETE' },
      ),
    )
    expect(await screen.findByText('Revoked')).toBeInTheDocument()
  })
})
