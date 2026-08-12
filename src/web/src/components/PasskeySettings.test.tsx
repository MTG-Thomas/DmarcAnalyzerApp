import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, fetchJson: vi.fn() }
})
vi.mock('@/lib/webauthn', async () => {
  const actual = await vi.importActual<typeof import('@/lib/webauthn')>('@/lib/webauthn')
  return { ...actual, createPasskey: vi.fn() }
})

import { PasskeySettings } from '@/components/PasskeySettings'
import { ApiError, fetchJson } from '@/lib/api'
import { createPasskey, type PasskeyCredential } from '@/lib/webauthn'

const existing: PasskeyCredential = {
  id: 'passkey-1', name: 'Work laptop', createdAtUtc: '2026-08-12T00:00:00Z',
  lastUsedAtUtc: null, isBackupEligible: true, isBackedUp: true,
}

describe('passkey settings', () => {
  beforeEach(() => vi.clearAllMocks())

  it('lists current-user passkeys without credential material', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce({ passkeys: true }).mockResolvedValueOnce({ passkeys: [existing] })
    render(<PasskeySettings />)
    expect(await screen.findByText('Work laptop')).toBeInTheDocument()
    expect(screen.getByText('Backed up')).toBeInTheDocument()
    expect(screen.queryByText(/credential-id|challenge/i)).not.toBeInTheDocument()
  })

  it('creates a named passkey through options and completion', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce({ passkeys: true })
      .mockResolvedValueOnce({ passkeys: [] })
      .mockResolvedValueOnce({ challenge: 'AQID', user: { id: 'AQID', name: 'operator', displayName: 'Operator' }, rp: { name: 'DMARC Analyzer' }, pubKeyCredParams: [] })
      .mockResolvedValueOnce(existing)
    vi.mocked(createPasskey).mockResolvedValue({ id: 'credential-id' } as never)

    render(<PasskeySettings />)
    fireEvent.click(await screen.findByRole('button', { name: 'Add passkey' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Work laptop' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add passkey' }))

    expect(await screen.findByText('Work laptop was added.')).toBeInTheDocument()
    expect(fetchJson).toHaveBeenNthCalledWith(3, '/api/v1/passkeys/options', { method: 'POST' })
    expect(fetchJson).toHaveBeenNthCalledWith(4, '/api/v1/passkeys', expect.objectContaining({
      method: 'POST', body: JSON.stringify({ name: 'Work laptop', credential: { id: 'credential-id' } }),
    }))
  })

  it('confirms removal and preserves other sign-in methods', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce({ passkeys: true }).mockResolvedValueOnce({ passkeys: [existing] }).mockResolvedValueOnce(undefined)
    render(<PasskeySettings />)
    fireEvent.click(await screen.findByRole('button', { name: 'Remove Work laptop' }))
    expect(screen.getByRole('alertdialog')).toHaveFocus()
    expect(screen.getByText(/Other sign-in methods remain unchanged/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Confirm remove' }))
    await waitFor(() => expect(fetchJson).toHaveBeenCalledWith('/api/v1/passkeys/passkey-1', { method: 'DELETE' }))
    expect(await screen.findByText('Work laptop was removed.')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Add passkey' })).toHaveFocus())
  })

  it('returns focus to the passkey row when removal is cancelled', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce({ passkeys: true }).mockResolvedValueOnce({ passkeys: [existing] })
    render(<PasskeySettings />)
    const remove = await screen.findByRole('button', { name: 'Remove Work laptop' })
    fireEvent.click(remove)
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(remove).toHaveFocus())
  })

  it('gives the dedicated recent-auth recovery without exposing server detail', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce({ passkeys: true })
      .mockResolvedValueOnce({ passkeys: [] })
      .mockRejectedValueOnce(new ApiError('recent authentication required', 403))
    render(<PasskeySettings />)
    fireEvent.click(await screen.findByRole('button', { name: 'Add passkey' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Work laptop' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add passkey' }))
    expect(await screen.findByText(/Sign out and sign back in/i)).toBeInTheDocument()
    expect(screen.queryByText('recent authentication required')).not.toBeInTheDocument()
  })

  it('keeps unexpected server errors secret-safe and recoverable', async () => {
    vi.mocked(fetchJson)
      .mockResolvedValueOnce({ passkeys: true })
      .mockResolvedValueOnce({ passkeys: [] })
      .mockRejectedValueOnce(new ApiError('credential id secret leaked', 500))
    render(<PasskeySettings />)
    fireEvent.click(await screen.findByRole('button', { name: 'Add passkey' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Work laptop' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add passkey' }))
    expect(await screen.findByText('The passkey could not be added. Try again.')).toBeInTheDocument()
    expect(screen.queryByText(/credential id secret leaked/i)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Add passkey' })).toBeEnabled()
  })

  it('explains disabled passkeys without calling the lifecycle API', async () => {
    vi.mocked(fetchJson).mockResolvedValueOnce({ passkeys: false })
    render(<PasskeySettings />)
    expect(await screen.findByText('Passkeys are not enabled for this installation.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add passkey' })).not.toBeInTheDocument()
    expect(fetchJson).toHaveBeenCalledTimes(1)
  })
})
