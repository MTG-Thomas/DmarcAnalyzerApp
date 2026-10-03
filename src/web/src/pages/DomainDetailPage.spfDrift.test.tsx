import { fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { SpfDriftState } from '@/lib/analytics'

vi.mock('@/lib/api', () => ({
  fetchJson: vi.fn(),
  ApiError: class extends Error {},
}))

const { authUser } = vi.hoisted(() => ({ authUser: { role: 'agency_admin' } }))
vi.mock('@/lib/auth-context', () => ({
  useAuth: () => ({ user: authUser }),
}))

import { fetchJson } from '@/lib/api'
import { SpfDriftCard, SpfDriftView } from '@/pages/DomainDetailPage'

const domainId = '3fa85f64-5717-4562-b3fc-2c963f66afa6'

const moved: SpfDriftState = {
  domainId,
  name: 'acme.example',
  checked: true,
  spfRecordStatus: 'found',
  rawRecord: 'v=spf1 include:mid.example.com -all',
  dependencies: [
    { domain: 'mid.example.com', record: 'v=spf1 ip4:192.0.2.1 -all', hash: 'new' },
  ],
  previousDependencies: [
    { domain: 'mid.example.com', record: 'v=spf1 ip4:198.51.100.0/24 -all', hash: 'old' },
  ],
  dependencyChangedAtUtc: new Date(Date.now() - 3600_000).toISOString(),
  candidateStatus: 'ready',
  candidateText: 'v=spf1 ip4:192.0.2.1 -all',
  previousCandidateStatus: 'ready',
  previousCandidateText: 'v=spf1 ip4:198.51.100.0/24 -all',
  candidateChangedAtUtc: null,
  publishedLookups: 2,
  candidateLookups: 0,
  candidateLength: 30,
  publishedOverBudget: false,
  previousPublishedLookups: 1,
  previousCandidateLookups: 0,
  previousCandidateLength: 34,
  previousPublishedOverBudget: false,
  issues: [],
  lastCheckedAtUtc: new Date().toISOString(),
  lastChangedAtUtc: new Date(Date.now() - 3600_000).toISOString(),
  lastSuccessAtUtc: new Date().toISOString(),
  consecutiveFailures: 0,
}

describe('SpfDriftView', () => {
  it('renders the old→new diff with the moved numbers', () => {
    render(<SpfDriftView state={moved} />)

    expect(screen.getByText('What changed')).toBeInTheDocument()
    // Once in the diff, once in the current snapshot below it.
    expect(screen.getAllByText('mid.example.com')).toHaveLength(2)
    expect(screen.getByText('Changed')).toBeInTheDocument()
    expect(screen.getByText(/198\.51\.100\.0\/24/)).toBeInTheDocument()
    // The new record renders in the diff, the snapshot, and the candidate text.
    expect(screen.getAllByText(/192\.0\.2\.1/)).toHaveLength(3)
    expect(screen.getByText(/lookups 1 → 2/)).toBeInTheDocument()
    expect(screen.getByText(/candidate 34 → 30 characters/)).toBeInTheDocument()
  })

  it('marks a failed check as last-known with unknown freshness', () => {
    render(
      <SpfDriftView
        state={{
          ...moved,
          spfRecordStatus: 'lookup_failed',
          previousDependencies: [],
          dependencyChangedAtUtc: null,
          lastSuccessAtUtc: new Date(Date.now() - 86400_000).toISOString(),
          consecutiveFailures: 3,
        }}
      />,
    )

    expect(screen.getByText('Check failed')).toBeInTheDocument()
    expect(screen.getByText(/showing last known state/)).toBeInTheDocument()
    expect(screen.getByText(/3 failed checks — freshness unknown/)).toBeInTheDocument()
    expect(screen.queryByText('What changed')).not.toBeInTheDocument()
  })

  it('renders the candidate flip and refusal note', () => {
    render(
      <SpfDriftView
        state={{
          ...moved,
          previousDependencies: [],
          dependencyChangedAtUtc: null,
          candidateStatus: 'refused',
          candidateText: null,
          candidateChangedAtUtc: new Date(Date.now() - 3600_000).toISOString(),
        }}
      />,
    )

    expect(screen.getByText('Refused')).toBeInTheDocument()
    expect(screen.getByText(/was ready/)).toBeInTheDocument()
    expect(screen.getByText(/no longer builds/)).toBeInTheDocument()
  })

  it('stays quiet before the first check and without an SPF record', () => {
    const { rerender } = render(<SpfDriftView state={{ ...moved, checked: false }} />)
    expect(screen.getByText(/Not checked yet/)).toBeInTheDocument()

    rerender(<SpfDriftView state={{ ...moved, spfRecordStatus: 'missing', rawRecord: null }} />)
    expect(screen.getByText('No SPF record')).toBeInTheDocument()
  })
})

describe('SpfDriftCard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    authUser.role = 'agency_admin'
  })

  it('loads the persisted state on mount', async () => {
    vi.mocked(fetchJson).mockResolvedValue(moved)
    render(<SpfDriftCard domainId={domainId} />)

    expect(await screen.findByText('SPF drift monitoring')).toBeInTheDocument()
    expect(await screen.findByText('What changed')).toBeInTheDocument()
    expect(vi.mocked(fetchJson)).toHaveBeenCalledWith(
      `/api/v1/analytics/domains/${domainId}/spf-drift`,
    )
  })

  it('rechecks on demand for staff', async () => {
    vi.mocked(fetchJson).mockResolvedValue(moved)
    render(<SpfDriftCard domainId={domainId} />)

    fireEvent.click(await screen.findByRole('button', { name: 'Recheck now' }))

    expect(vi.mocked(fetchJson)).toHaveBeenCalledWith(
      `/api/v1/analytics/domains/${domainId}/spf-drift/recheck`,
      { method: 'POST' },
    )
  })

  it('hides the recheck button from viewers', async () => {
    authUser.role = 'client_viewer'
    vi.mocked(fetchJson).mockResolvedValue(moved)
    render(<SpfDriftCard domainId={domainId} />)

    expect(await screen.findByText('What changed')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Recheck now' })).not.toBeInTheDocument()
  })

  it('shows the failure instead of a stale panel', async () => {
    vi.mocked(fetchJson).mockRejectedValue(new Error('DNS timed out'))
    render(<SpfDriftCard domainId={domainId} />)

    expect(await screen.findByText('DNS timed out')).toBeInTheDocument()
    expect(screen.queryByText('What changed')).not.toBeInTheDocument()
  })
})
