import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { ClientViewPage } from '@/pages/ClientViewPage'

const token = 'dmarc_ml_v1.abcdefghijklmnopqrstuv.abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG'

function renderPage(search: string) {
  window.history.pushState({}, '', `/client-view${search}`)
  return render(
    <MemoryRouter initialEntries={[`/client-view${search}`]}>
      <ClientViewPage />
    </MemoryRouter>,
  )
}

describe('anonymous client view', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('asks for the full link when no token is present', async () => {
    renderPage('')

    expect(await screen.findByText(/this link did not work/i)).toBeInTheDocument()
    expect(screen.getByText(/no access token/i)).toBeInTheDocument()
  })

  it('explains an expired or revoked link without leaking the token', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ error: 'not authenticated' }), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderPage(`?token=${encodeURIComponent(token)}`)

    expect(await screen.findByText(/expired, revoked, or mistyped/i)).toBeInTheDocument()
    expect(screen.queryByText(token)).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/clients',
      expect.objectContaining({
        headers: expect.objectContaining({ Authorization: `Bearer ${token}` }),
      }),
    )
  })

  it('renders the shared client report without a session', async () => {
    const fetchMock = vi.fn(async (url: string) => {
      if (url === '/api/v1/clients') {
        return new Response(
          JSON.stringify([{ id: 'client-1', name: 'Acme Inc', slug: 'acme', isActive: true }]),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }
      if (url.startsWith('/api/v1/analytics/summary')) {
        return new Response(
          JSON.stringify({
            window: {
              days: 30,
              beginUtc: '2026-03-01T00:00:00Z',
              endUtc: '2026-03-31T00:00:00Z',
              anchoredToLatestData: true,
            },
            totals: {
              domains: 2,
              activeDomains: 2,
              reports: 10,
              messages: 1200,
              compliantMessages: 1140,
              complianceRate: 0.95,
              dkimPassRate: 0.9,
              spfPassRate: 0.85,
              failingSources: 1,
            },
            trend: [{ date: '2026-03-31', messages: 100, compliant: 95, failed: 5 }],
            topFailingDomains: [],
            topReporters: [],
            dispositions: { none: 60, pass: 0, quarantine: 30, reject: 10 },
            mailboxes: null,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }
      if (url.startsWith('/api/v1/analytics/domains')) {
        return new Response(
          JSON.stringify([
            {
              domainId: 'domain-1',
              name: 'acme.example',
              isActive: true,
              clientId: 'client-1',
              clientName: 'Acme Inc',
              clientSlug: 'acme',
              messages: 1200,
              compliantMessages: 1140,
              complianceRate: 0.95,
              dkimPassRate: 0.9,
              spfPassRate: 0.85,
              reports: 10,
              sources: 3,
              reporters: 2,
              quarantined: 30,
              rejected: 10,
              lastReportEndUtc: '2026-03-31T00:00:00Z',
              status: 'aligned',
              publishedPolicy: 'reject',
              subdomainPolicy: null,
              publishedPct: 100,
              dkimAlignment: 'relaxed',
              spfAlignment: 'relaxed',
              dnsLookupStatus: 'found',
              dnsPolicyInheritedFrom: null,
              dnsCheckedAtUtc: '2026-03-31T00:00:00Z',
              enforcementStatus: 'enforced',
            },
          ]),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        )
      }
      return new Response('not found', { status: 404 })
    })
    vi.stubGlobal('fetch', fetchMock)

    renderPage(`?token=${encodeURIComponent(token)}`)

    expect(await screen.findByText(/Acme Inc — DMARC report/)).toBeInTheDocument()
    expect(screen.getByText('acme.example')).toBeInTheDocument()
    expect(screen.getByText('p=reject')).toBeInTheDocument()
    // The token authenticates the API calls but is never rendered.
    expect(screen.queryByText(token)).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })
})
