import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({ fetchJson: vi.fn(), setUnauthorizedHandler: vi.fn() }))
vi.mock('@/lib/webauthn', () => ({ requestPasskey: vi.fn() }))

import { AuthProvider } from '@/components/AuthProvider'
import { fetchJson } from '@/lib/api'
import { useAuth } from '@/lib/auth-context'
import { requestPasskey } from '@/lib/webauthn'

const user = {
  id: 'user-1', email: 'operator@example.com', displayName: 'Operator', role: 'agency_admin',
  isActive: true, lastLoginAtUtc: null, createdAtUtc: '2026-08-12T00:00:00Z', updatedAtUtc: '2026-08-12T00:00:00Z',
}

function Harness() {
  const auth = useAuth()
  return <><span>{auth.status}</span><button onClick={() => void auth.loginWithPasskey()}>Passkey</button></>
}

describe('passkey session transition', () => {
  beforeEach(() => vi.clearAllMocks())

  it('requests options, completes the assertion, and authenticates the session', async () => {
    vi.mocked(fetchJson).mockImplementation(async (url) => {
      if (url === '/api/v1/auth/me') throw new Error('no session')
      if (url === '/api/v1/auth/passkeys/options') return { challenge: 'AQID' }
      return { user }
    })
    vi.mocked(requestPasskey).mockResolvedValue({ id: 'credential-id' } as never)

    render(<AuthProvider><Harness /></AuthProvider>)
    await screen.findByText('unauthenticated')
    fireEvent.click(screen.getByRole('button', { name: 'Passkey' }))

    await screen.findByText('authenticated')
    expect(fetchJson).toHaveBeenCalledWith('/api/v1/auth/passkeys/options', { method: 'POST' })
    expect(fetchJson).toHaveBeenLastCalledWith('/api/v1/auth/passkeys/complete', expect.objectContaining({
      method: 'POST', body: JSON.stringify({ id: 'credential-id' }),
    }))
    await waitFor(() => expect(requestPasskey).toHaveBeenCalled())
  })
})
