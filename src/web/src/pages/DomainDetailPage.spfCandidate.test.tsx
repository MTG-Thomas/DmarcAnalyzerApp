import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { SpfCandidate } from '@/lib/analytics'
import { SpfCandidateView } from '@/pages/DomainDetailPage'

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
