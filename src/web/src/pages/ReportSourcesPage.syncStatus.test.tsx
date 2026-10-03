import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

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
import type {
  Client,
  ReportSource,
  SyncRequestResponse,
  SyncRequestStatusResponse,
} from '@/lib/entities'
import { ReportSourcesPage } from '@/pages/ReportSourcesPage'

const mailboxSource: ReportSource = {
  id: 'source-mailbox',
  name: 'Reports inbox',
  protocol: 'imap',
  host: 'mail.example.test',
  port: 993,
  useTls: true,
  username: 'reports@example.test',
  defaultClientId: 'client-1',
  defaultClientName: 'Example client',
  isActive: true,
  deleteAfterRetention: false,
  oldestMessageAtUtc: null,
  s3Bucket: null,
  s3Prefix: null,
  s3Region: null,
  s3Endpoint: null,
  s3ForcePathStyle: false,
}

const secondSource: ReportSource = {
  ...mailboxSource,
  id: 'source-second',
  name: 'Second inbox',
  host: 'mail2.example.test',
  port: 995,
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

let postError: Error | null = null
let statusResponder: (url: string) => SyncRequestStatusResponse = () => {
  throw new Error('no status responder installed')
}

const statusUrlFor = (sourceId: string) =>
  `/api/v1/report-sources/${sourceId}/sync-requests/req-1`

const installMocks = (sources: ReportSource[]) => {
  vi.mocked(fetchJson).mockImplementation(async (url: string, init?: RequestInit) => {
    if (url === '/api/v1/clients') return [client] as never
    if (url === '/api/v1/report-sources') return sources as never
    if (url === '/api/v1/mailbox-health') return [] as never
    if (url.startsWith('/api/v1/mailbox-sync-runs')) return [] as never
    if (url.endsWith('/sync') && init?.method === 'POST') {
      if (postError) throw postError
      const sourceId = url.split('/')[4] ?? ''
      const response: SyncRequestResponse = {
        requestId: 'req-1',
        status: 'queued',
        statusUrl: statusUrlFor(sourceId),
      }
      return response as never
    }
    if (url.includes('/sync-requests/')) return statusResponder(url) as never
    throw new Error(`unexpected request: ${url} ${init?.method ?? 'GET'}`)
  })
}

const syncStatus = (
  overrides: Partial<SyncRequestStatusResponse> & {
    status: SyncRequestStatusResponse['status']
  },
): SyncRequestStatusResponse => ({
  requestId: 'req-1',
  reportSourceId: mailboxSource.id,
  createdAtUtc: '2026-08-11T12:00:00Z',
  startedAtUtc: '2026-08-11T12:00:01Z',
  finishedAtUtc: null,
  attempts: 1,
  error: null,
  summary: null,
  ...overrides,
})

const statusCallCount = () =>
  vi
    .mocked(fetchJson)
    .mock.calls.filter(
      ([url]) => typeof url === 'string' && url.includes('/sync-requests/'),
    ).length

const advance = async (ms: number) => {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms)
  })
}

describe('manual sync status', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    postError = null
    statusResponder = () => {
      throw new Error('no status responder installed')
    }
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('shows live sync status and stops polling once completed', async () => {
    installMocks([mailboxSource])
    const responses = [
      syncStatus({ status: 'queued' }),
      syncStatus({ status: 'running' }),
      syncStatus({
        status: 'completed',
        finishedAtUtc: '2026-08-11T12:01:00Z',
        summary: { messagesScanned: 1204, reportsInserted: 37, parseFailures: 0 },
      }),
    ]
    let calls = 0
    statusResponder = () => responses[Math.min(calls++, responses.length - 1)]

    render(<ReportSourcesPage />)
    const syncButton = await screen.findByRole('button', { name: 'Sync now' })

    vi.useFakeTimers()
    fireEvent.click(syncButton)
    await advance(0)
    expect(screen.getByText('Sync status')).toBeInTheDocument()
    expect(screen.getByText('Queued')).toBeInTheDocument()

    await advance(5000)
    expect(screen.getByText('Running')).toBeInTheDocument()

    await advance(5000)
    expect(screen.getByText('Completed')).toBeInTheDocument()
    expect(screen.getByText('Messages scanned')).toBeInTheDocument()
    expect(screen.getByText('1,204')).toBeInTheDocument()
    expect(screen.getByText('Reports inserted')).toBeInTheDocument()
    expect(screen.getByText('37')).toBeInTheDocument()
    expect(screen.getByText('Parse failures')).toBeInTheDocument()

    expect(statusCallCount()).toBe(3)
    await advance(30000)
    expect(statusCallCount()).toBe(3)
  })

  it('renders failure status with error text and attempt count', async () => {
    installMocks([mailboxSource])
    const responses = [
      syncStatus({ status: 'running' }),
      syncStatus({
        status: 'failed',
        finishedAtUtc: '2026-08-11T12:01:00Z',
        attempts: 2,
        error: 'Connection refused',
      }),
    ]
    let calls = 0
    statusResponder = () => responses[Math.min(calls++, responses.length - 1)]

    render(<ReportSourcesPage />)
    const syncButton = await screen.findByRole('button', { name: 'Sync now' })

    vi.useFakeTimers()
    fireEvent.click(syncButton)
    await advance(0)
    await advance(5000)

    expect(screen.getByText('Failed')).toBeInTheDocument()
    expect(screen.getByText('Connection refused')).toBeInTheDocument()
    expect(screen.getByText('Attempts: 2')).toBeInTheDocument()

    expect(statusCallCount()).toBe(2)
    await advance(30000)
    expect(statusCallCount()).toBe(2)
  })

  it('tracks partial and cancelled outcomes per source', async () => {
    installMocks([mailboxSource, secondSource])
    statusResponder = (url) =>
      url.includes(secondSource.id)
        ? syncStatus({
            status: 'cancelled',
            reportSourceId: secondSource.id,
            finishedAtUtc: '2026-08-11T12:01:00Z',
          })
        : syncStatus({
            status: 'partial',
            finishedAtUtc: '2026-08-11T12:01:00Z',
            summary: { messagesScanned: 40 },
          })

    render(<ReportSourcesPage />)
    const syncButtons = await screen.findAllByRole('button', { name: 'Sync now' })
    expect(syncButtons).toHaveLength(2)

    vi.useFakeTimers()
    fireEvent.click(syncButtons[0])
    fireEvent.click(syncButtons[1])
    await advance(0)

    expect(screen.getByText('Partial')).toBeInTheDocument()
    expect(screen.getByText('Cancelled')).toBeInTheDocument()
    expect(screen.getByText('Messages scanned')).toBeInTheDocument()
    expect(screen.getByText('40')).toBeInTheDocument()
  })

  it('stops polling after ten minutes without a terminal status', async () => {
    installMocks([mailboxSource])
    statusResponder = () => syncStatus({ status: 'running' })

    render(<ReportSourcesPage />)
    const syncButton = await screen.findByRole('button', { name: 'Sync now' })

    vi.useFakeTimers()
    fireEvent.click(syncButton)
    await advance(0)
    expect(screen.getByText('Running')).toBeInTheDocument()

    await advance(10 * 60 * 1000)
    expect(screen.getByText(/timed out after 10 minutes/)).toBeInTheDocument()

    const count = statusCallCount()
    expect(count).toBeGreaterThan(0)
    await advance(60000)
    expect(statusCallCount()).toBe(count)

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
    expect(screen.queryByText('Sync status')).not.toBeInTheDocument()
  })

  it('stops polling when the page unmounts', async () => {
    installMocks([mailboxSource])
    statusResponder = () => syncStatus({ status: 'running' })

    const { unmount } = render(<ReportSourcesPage />)
    const syncButton = await screen.findByRole('button', { name: 'Sync now' })

    vi.useFakeTimers()
    fireEvent.click(syncButton)
    await advance(0)
    expect(statusCallCount()).toBe(1)

    unmount()
    await advance(30000)
    expect(statusCallCount()).toBe(1)
  })

  it('shows an error when the sync request is rejected', async () => {
    installMocks([mailboxSource])
    postError = new Error('Sync already running')

    render(<ReportSourcesPage />)
    const syncButton = await screen.findByRole('button', { name: 'Sync now' })
    fireEvent.click(syncButton)

    expect(await screen.findByText('Sync already running')).toBeInTheDocument()
    expect(screen.queryByText('Sync status')).not.toBeInTheDocument()
  })
})
