import { fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({
  fetchJson: vi.fn(),
}))
vi.mock('@/lib/auth-context', () => ({
  useAuth: () => ({
    status: 'authenticated',
    user: { id: 'u1', email: 'admin@example.test', displayName: 'Admin', role: 'agency_admin' },
    login: vi.fn(),
    logout: vi.fn(),
  }),
}))

import { fetchJson } from '@/lib/api'
import type { ReportSource } from '@/lib/entities'
import { ReportSourcesPage } from '@/pages/ReportSourcesPage'

const apiSource: ReportSource = {
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

const mailboxSource: ReportSource = {
  ...apiSource,
  id: 'source-mailbox',
  name: 'Reports inbox',
  protocol: 'imap',
  host: 'mail.example.test',
  port: 993,
  useTls: true,
  username: 'reports@example.test',
}

describe('API key discoverability', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(fetchJson).mockImplementation(async (url: string) => {
      if (url === '/api/v1/clients') return [] as never
      if (url === '/api/v1/report-sources') return [apiSource, mailboxSource] as never
      if (url === '/api/v1/mailbox-health') return [] as never
      if (url.startsWith('/api/v1/mailbox-sync-runs')) return [] as never
      if (url === `/api/v1/report-sources/${apiSource.id}/credentials`) return [] as never
      throw new Error(`unexpected request: ${url}`)
    })
  })

  it('offers API key management only on API source rows', async () => {
    render(<ReportSourcesPage />)

    const manageButtons = await screen.findAllByRole('button', { name: 'API keys' })
    expect(manageButtons).toHaveLength(1)

    fireEvent.click(manageButtons[0])
    expect(await screen.findByRole('heading', { name: `API keys for ${apiSource.name}` })).toBeInTheDocument()
    expect(fetchJson).toHaveBeenCalledWith(`/api/v1/report-sources/${apiSource.id}/credentials`)
  })
})
