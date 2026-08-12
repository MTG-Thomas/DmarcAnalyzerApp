import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({
  fetchJson: vi.fn(),
}))
vi.mock('@/lib/auth-context', () => ({
  useAuth: () => ({
    status: 'authenticated',
    user: { id: 'u1', email: 'admin@example.test', displayName: 'Admin', role: 'agency_admin' },
    login: vi.fn(),
    loginWithPasskey: vi.fn(),
    logout: vi.fn(),
  }),
}))

import { fetchJson } from '@/lib/api'
import type { Client, MailboxHealth, MailboxSyncRun, ReportSource } from '@/lib/entities'
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

const client: Client = {
  id: 'client-1',
  name: 'Example client',
  slug: 'example-client',
  isActive: true,
  retentionMonths: 12,
  legalHold: false,
  alertsEnabled: true,
  alertComplianceDropPercent: null,
  alertMinMessages: null,
  timezone: 'UTC',
}

const mailboxHealth: MailboxHealth = {
  reportSourceId: mailboxSource.id,
  name: mailboxSource.name,
  isActive: true,
  lastSuccessSyncAtUtc: '2026-08-10T12:00:00Z',
  lastProcessedUid: 42,
  lastProcessedUidValidity: 7,
  lastRunStatus: 'failed',
  lastRunStartedAtUtc: '2026-08-11T12:00:00Z',
  lastRunFinishedAtUtc: '2026-08-11T12:01:00Z',
  lastRunError: 'Mailbox unavailable',
  lastRunMessagesScanned: 8,
  lastRunAttachmentsProcessed: 5,
  lastRunReportsInserted: 3,
  lastRunReportsSkippedAsDuplicate: 1,
  lastRunParseFailures: 1,
  lastRunTlsReportsInserted: 2,
  lastRunTlsReportsSkippedAsDuplicate: 1,
}

const syncRun: MailboxSyncRun = {
  id: 'run-1',
  reportSourceId: mailboxSource.id,
  trigger: 'manual',
  status: 'failed',
  startedAtUtc: '2026-08-11T12:00:00Z',
  finishedAtUtc: '2026-08-11T12:01:00Z',
  messagesScanned: 8,
  attachmentsProcessed: 5,
  reportsInserted: 3,
  reportsSkippedAsDuplicate: 1,
  parseFailures: 1,
  tlsReportsInserted: 2,
  tlsReportsSkippedAsDuplicate: 1,
  error: 'Mailbox unavailable',
  createdAtUtc: '2026-08-11T12:00:00Z',
}

describe('API key discoverability', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(fetchJson).mockImplementation(async (url: string) => {
      if (url === '/api/v1/clients') return [client] as never
      if (url === '/api/v1/report-sources') return [apiSource, mailboxSource] as never
      if (url === '/api/v1/mailbox-health') return [mailboxHealth] as never
      if (url.startsWith('/api/v1/mailbox-sync-runs')) return [syncRun] as never
      if (url === `/api/v1/report-sources/${apiSource.id}/credentials`) return [] as never
      if (url.startsWith('/api/v1/report-sources')) return undefined as never
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

  it('creates, edits, and syncs through report-source routes', async () => {
    render(<ReportSourcesPage />)
    await screen.findAllByText('mail.example.test')

    fireEvent.click(screen.getByRole('button', { name: 'Add source' }))
    fireEvent.change(screen.getByLabelText('Source name'), { target: { value: 'API intake' } })
    fireEvent.change(screen.getByLabelText('Protocol'), { target: { value: 'api' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(fetchJson).toHaveBeenCalledWith(
      '/api/v1/report-sources',
      expect.objectContaining({ method: 'POST' }),
    ))

    fireEvent.click(screen.getAllByRole('button', { name: 'Edit' })[1])
    fireEvent.change(screen.getByLabelText('Source name'), { target: { value: 'Updated inbox' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(fetchJson).toHaveBeenCalledWith(
      `/api/v1/report-sources/${mailboxSource.id}`,
      expect.objectContaining({ method: 'PATCH' }),
    ))

    fireEvent.click(screen.getByRole('button', { name: 'Sync now' }))
    await waitFor(() => expect(fetchJson).toHaveBeenCalledWith(
      `/api/v1/report-sources/${mailboxSource.id}/sync`,
      { method: 'POST' },
    ))
  })

  it('renders mailbox evidence and applies search and operations filters', async () => {
    render(<ReportSourcesPage />)

    expect(await screen.findAllByText('Mailbox unavailable')).toHaveLength(3)
    expect(screen.getByText('8/5/3/1/1 · tls 2/1')).toBeInTheDocument()

    fireEvent.change(screen.getByPlaceholderText('Search sources'), { target: { value: 'missing' } })
    expect(screen.getByText('No report sources found for the current search.')).toBeInTheDocument()

    fireEvent.change(screen.getByDisplayValue('All mailboxes'), { target: { value: 'parse-failures' } })
    expect(screen.getByText('Parse failures: 1')).toBeInTheDocument()
  })
})
