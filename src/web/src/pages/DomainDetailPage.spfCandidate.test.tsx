import { fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { RecordInspection, SpfCandidate } from '@/lib/analytics'

vi.mock('@/lib/api', () => ({
  fetchJson: vi.fn(),
  ApiError: class extends Error {},
}))

import { fetchJson } from '@/lib/api'
import {
  RecordInspectionCard,
  SpfCandidatePanel,
  SpfCandidateView,
} from '@/pages/DomainDetailPage'

const ready: SpfCandidate = {
  status: 'ready',
  original: 'v=spf1 include:static.example.com include:_spf.google.com -all',
  candidate: 'v=spf1 ip4:198.51.100.7 include:_spf.google.com -all',
  terms: [
    {
      originalText: 'include:static.example.com',
      outcome: 'expanded',
      reason: null,
      expandedTerms: ['ip4:198.51.100.7'],
    },
    {
      originalText: 'include:_spf.google.com',
      outcome: 'preserved',
      reason: 'Volatile provider set — deliberately not expanded, kept as a stub include.',
      expandedTerms: [],
    },
  ],
  reasons: [],
  originalLookups: 2,
  candidateLookups: 1,
  candidateLength: 54,
  txtSegments: 1,
}

const refused: SpfCandidate = {
  status: 'refused',
  original: 'v=spf1 exists:%{i}.example.com -all',
  candidate: null,
  terms: [
    {
      originalText: 'exists:%{i}.example.com',
      outcome: 'preserved',
      reason: 'Dynamic DNS existence test — cannot be flattened, kept.',
      expandedTerms: [],
    },
  ],
  reasons: ['Nothing could be expanded.', 'exists:%{i}.example.com — dynamic, kept.'],
  originalLookups: 1,
  candidateLookups: 0,
  candidateLength: 0,
  txtSegments: 0,
}

describe('SpfCandidateView', () => {
  it('renders the candidate with lookup math and the per-term diff', () => {
    render(<SpfCandidateView candidate={ready} />)

    expect(
      screen.getByText('v=spf1 ip4:198.51.100.7 include:_spf.google.com -all'),
    ).toBeInTheDocument()
    expect(screen.getByText(/2→1 lookups/)).toBeInTheDocument()
    expect(screen.getByText(/54 characters/)).toBeInTheDocument()
    expect(screen.getByText(/1 TXT segment/)).toBeInTheDocument()
    expect(screen.getByText('Expanded')).toBeInTheDocument()
    expect(screen.getByText('Kept as-is')).toBeInTheDocument()
    expect(screen.getByText(/Volatile provider set/)).toBeInTheDocument()
  })

  it('renders refusal reasons without a candidate block', () => {
    render(<SpfCandidateView candidate={refused} />)

    expect(screen.getByText('Nothing could be expanded.')).toBeInTheDocument()
    expect(screen.queryByText(/lookups/)).not.toBeInTheDocument()
  })
})

const domainId = '3fa85f64-5717-4562-b3fc-2c963f66afa6'

describe('SpfCandidatePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('fetches nothing until asked, then renders the candidate', async () => {
    vi.mocked(fetchJson).mockResolvedValue(ready)
    render(<SpfCandidatePanel domainId={domainId} />)

    expect(vi.mocked(fetchJson)).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Generate candidate' }))

    expect(
      await screen.findByText('v=spf1 ip4:198.51.100.7 include:_spf.google.com -all'),
    ).toBeInTheDocument()
    expect(vi.mocked(fetchJson)).toHaveBeenCalledWith(
      `/api/v1/analytics/domains/${domainId}/spf-candidate`,
    )
    expect(screen.getByRole('button', { name: 'Regenerate candidate' })).toBeInTheDocument()
  })

  it('shows the failure instead of a stale candidate', async () => {
    vi.mocked(fetchJson).mockRejectedValue(new Error('DNS timed out'))
    render(<SpfCandidatePanel domainId={domainId} />)

    fireEvent.click(screen.getByRole('button', { name: 'Generate candidate' }))

    expect(await screen.findByText('DNS timed out')).toBeInTheDocument()
    expect(screen.queryByText(/lookups/)).not.toBeInTheDocument()
  })

  it('shows a busy indicator while expanding', async () => {
    let resolve!: (value: SpfCandidate) => void
    vi.mocked(fetchJson).mockReturnValue(
      new Promise<SpfCandidate>((res) => {
        resolve = res
      }),
    )
    render(<SpfCandidatePanel domainId={domainId} />)

    fireEvent.click(screen.getByRole('button', { name: 'Generate candidate' }))
    expect(await screen.findByText(/Expanding includes/)).toBeInTheDocument()

    resolve(ready)
    expect(await screen.findByText(/2→1 lookups/)).toBeInTheDocument()
  })
})

describe('RecordInspectionCard candidate mount', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  const inspection: RecordInspection = {
    domainId,
    name: 'acme.example',
    dmarc: {
      status: 'found',
      raw: 'v=DMARC1; p=reject',
      policy: 'reject',
      subdomainPolicy: null,
      pct: 100,
      rua: null,
      ruf: null,
      dkimAlignment: null,
      spfAlignment: null,
      issues: [],
      testing: null,
      publicSuffixDomain: null,
      nonExistentSubdomainPolicy: null,
    },
    spf: {
      status: 'found',
      raw: 'v=spf1 -all',
      recordCount: 1,
      lookupMechanisms: 0,
      allQualifier: '-',
      issues: [],
      recursiveLookups: 0,
      voidLookups: 0,
      overBudget: false,
      estimatedResponseBytes: null,
      dependencyTree: null,
    },
    observed: null,
    comparison: [],
    externalDestinations: [],
  }

  it('mounts the candidate panel when SPF is found', async () => {
    vi.mocked(fetchJson).mockResolvedValue(inspection)
    render(<RecordInspectionCard domainId={domainId} />)

    expect(await screen.findByText('Flattening candidate')).toBeInTheDocument()
  })

  it('omits the panel when SPF is missing', async () => {
    vi.mocked(fetchJson).mockResolvedValue({
      ...inspection,
      spf: { ...inspection.spf, status: 'missing', raw: null },
    })
    render(<RecordInspectionCard domainId={domainId} />)

    expect(await screen.findByText('SPF (live DNS)')).toBeInTheDocument()
    expect(screen.queryByText('Flattening candidate')).not.toBeInTheDocument()
  })
})
