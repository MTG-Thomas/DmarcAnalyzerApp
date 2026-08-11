import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { SourceDetail } from '@/lib/analytics'

vi.mock('@/lib/api', () => ({
  fetchJson: vi.fn(),
  ApiError: class extends Error {},
}))

import { fetchJson } from '@/lib/api'
import { SourceDetailPanel } from '@/pages/DomainDetailPage'

const detail: SourceDetail = {
  sourceIp: '192.0.2.10',
  messages: 17,
  compliantMessages: 10,
  complianceRate: 10 / 17,
  dispositions: { none: 3, pass: 7, quarantine: 5, reject: 2 },
  evaluated: [
    { dkim: 'pass', spf: 'fail', messages: 7 },
    { dkim: 'fail', spf: 'pass', messages: 3 },
    { dkim: 'fail', spf: 'fail', messages: 7 },
  ],
  headerFroms: [],
  envelopeFroms: [],
  dkimAuth: [],
  spfAuth: [],
  reporters: [],
  trend: [],
}

describe('Source detail dispositions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(fetchJson).mockResolvedValue(detail)
  })

  it('renders the RFC 9990 pass bucket alongside the v1 buckets', async () => {
    render(<SourceDetailPanel domainId="domain-1" sourceIp={detail.sourceIp} days={30} />)

    expect(await screen.findByText('pass · 7')).toBeInTheDocument()
    expect(screen.getByText('none · 3')).toBeInTheDocument()
    expect(screen.getByText('quarantine · 5')).toBeInTheDocument()
    expect(screen.getByText('reject · 2')).toBeInTheDocument()
  })
})
