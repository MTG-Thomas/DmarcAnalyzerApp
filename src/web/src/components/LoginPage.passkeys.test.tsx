import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const loginWithPasskey = vi.fn()
vi.mock('@/lib/auth-context', () => ({
  useAuth: () => ({ login: vi.fn(), loginWithPasskey, status: 'unauthenticated' }),
}))
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, fetchJson: vi.fn() }
})

import { LoginPage } from '@/components/LoginPage'
import { fetchJson } from '@/lib/api'
import { PasskeyBrowserError } from '@/lib/webauthn'

describe('passkey login', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(fetchJson).mockImplementation(async (url) => url.endsWith('/setup')
      ? { requiresBootstrap: false }
      : { local: true, passkeys: true, oidc: null })
  })

  it('shows passkey and password sign-in without requesting an identity', async () => {
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    expect(await screen.findByRole('button', { name: 'Sign in with a passkey' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByLabelText('Email')).toHaveValue('')
  })

  it('shows one separator between passkey and OIDC-only sign-in', async () => {
    vi.mocked(fetchJson).mockImplementation(async (url) => url.endsWith('/setup')
      ? { requiresBootstrap: false }
      : { local: false, passkeys: true, oidc: { enabled: true, displayName: 'Entra ID', loginUrl: '/oidc' } })
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await screen.findByRole('button', { name: 'Sign in with a passkey' })
    expect(screen.getAllByText('or')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Sign in with Entra ID' })).toBeInTheDocument()
  })

  it.each([
    [new PasskeyBrowserError('unsupported'), /not supported/i],
    [new PasskeyBrowserError('cancelled'), /was cancelled/i],
  ])('shows a recoverable browser failure', async (failure, message) => {
    loginWithPasskey.mockRejectedValueOnce(failure)
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    fireEvent.click(await screen.findByRole('button', { name: 'Sign in with a passkey' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(message)
    expect(screen.getByRole('button', { name: 'Sign in with a passkey' })).toBeEnabled()
  })
})
