import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'

import { BrandLogo } from '@/components/BrandLogo'
import { ComplianceBar } from '@/components/data/ComplianceBar'
import { PolicyBadge, type DmarcPolicy } from '@/components/data/PolicyBadge'
import { StatCard } from '@/components/data/StatCard'
import { TrendChart } from '@/components/data/TrendChart'
import { Badge } from '@/components/ui/badge'
import { Card } from '@/components/ui/card'
import { Icon } from '@/components/ui/icon'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import {
  DOMAIN_STATUS_META,
  ENFORCEMENT_STATUS_META,
  type AnalyticsSummary,
  type DomainAnalytics,
} from '@/lib/analytics'
import { ApiError } from '@/lib/api'
import type { Client } from '@/lib/entities'
import { formatCompact, formatFullDate, formatPercent, formatShortDate } from '@/lib/format'
import { usePageTitle } from '@/lib/use-page-title'

async function fetchWithToken<T>(url: string, token: string): Promise<T> {
  const response = await fetch(url, {
    credentials: 'include',
    headers: { Authorization: `Bearer ${token}` },
  })
  if (!response.ok) {
    let message = `Request failed (${response.status})`
    try {
      const payload = (await response.json()) as { error?: string }
      if (payload.error) message = payload.error
    } catch {
      // ignore json parse errors
    }
    throw new ApiError(message, response.status)
  }
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

function InvalidLink({ message }: { message: string }) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-surface-page px-4">
      <Card className="max-w-md text-center">
        <div className="mb-4 flex justify-center">
          <BrandLogo height={30} />
        </div>
        <h1 className="font-display text-xl font-bold tracking-tight text-body">This link did not work</h1>
        <p className="mt-2 text-sm text-secondary">{message}</p>
        <p className="mt-1 text-sm text-secondary">
          Links expire after a few days and stop working once revoked. Ask your provider for a fresh one.
        </p>
      </Card>
    </div>
  )
}

export function ClientViewPage() {
  usePageTitle('Client report')
  const [searchParams] = useSearchParams()
  const token = (searchParams.get('token') ?? '').trim()

  const [client, setClient] = useState<Client | null>(null)
  const [summary, setSummary] = useState<AnalyticsSummary | null>(null)
  const [domains, setDomains] = useState<DomainAnalytics[]>([])
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!token) {
      setBusy(false)
      setError('This link has no access token. Open the full link your provider sent you.')
      return
    }

    let cancelled = false
    const load = async () => {
      setBusy(true)
      setError(null)
      try {
        const [clients, loadedSummary, loadedDomains] = await Promise.all([
          fetchWithToken<Client[]>('/api/v1/clients', token),
          fetchWithToken<AnalyticsSummary>('/api/v1/analytics/summary?days=30', token),
          fetchWithToken<DomainAnalytics[]>('/api/v1/analytics/domains?days=30', token),
        ])
        if (cancelled) return
        if (clients.length !== 1) {
          setError('This link is not valid for exactly one client. Ask your provider for a fresh one.')
          return
        }
        setClient(clients[0])
        setSummary(loadedSummary)
        setDomains([...loadedDomains].sort((a, b) => a.name.localeCompare(b.name)))
      } catch (loadError) {
        if (cancelled) return
        if (loadError instanceof ApiError && (loadError.status === 401 || loadError.status === 403)) {
          setError('This link is expired, revoked, or mistyped.')
        } else {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load the report')
        }
      } finally {
        if (!cancelled) setBusy(false)
      }
    }

    void load()
    return () => {
      cancelled = true
    }
  }, [token])

  if (busy) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-surface-page">
        <Icon name="loader-circle" size={24} className="animate-spin text-secondary" aria-label="Loading" />
      </div>
    )
  }

  if (error || !client || !summary) {
    return <InvalidLink message={error ?? 'This link is expired, revoked, or mistyped.'} />
  }

  const totals = summary.totals
  const subtitle = summary.window.anchoredToLatestData
    ? `Data through ${formatFullDate(summary.window.endUtc)}`
    : `Last ${summary.window.days} days`

  return (
    <div className="min-h-screen bg-surface-page">
      <div className="mx-auto max-w-[1040px] px-4 py-5 sm:px-6 lg:px-8 lg:py-[26px]">
        <div className="mb-5 flex items-center justify-between gap-4">
          <BrandLogo height={28} />
          <Badge variant="neutral">Shared report</Badge>
        </div>

        <h1 className="font-display text-xl font-bold tracking-tight text-body">
          {client.name} — DMARC report
        </h1>
        <p className="mt-1 text-sm text-secondary">{subtitle}</p>

        <div className="mt-5 grid grid-cols-2 gap-3 lg:grid-cols-4">
          <StatCard label="Compliance" value={formatPercent(totals.complianceRate)} />
          <StatCard label="Messages" value={formatCompact(totals.messages)} />
          <StatCard label="Domains" value={formatCompact(totals.domains)} />
          <StatCard label="Failing sources" value={formatCompact(totals.failingSources)} />
        </div>

        <Card className="mt-3">
          <h2 className="mb-3 text-sm font-semibold text-body">Compliance trend</h2>
          <TrendChart
            data={summary.trend.map((point) => ({
              label: formatShortDate(point.date),
              pass: point.compliant,
              fail: point.failed,
            }))}
          />
        </Card>

        <Card pad={false} className="mt-3">
          <h2 className="px-5 pt-4 text-sm font-semibold text-body">Domains</h2>
          <div className="mt-2 overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Domain</TableHead>
                  <TableHead>Policy</TableHead>
                  <TableHead className="text-right">Compliance</TableHead>
                  <TableHead>Status</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {domains.map((domain, index) => {
                  const status = DOMAIN_STATUS_META[domain.status]
                  const enforcement = ENFORCEMENT_STATUS_META[domain.enforcementStatus]
                  return (
                    <TableRow key={domain.domainId} last={index === domains.length - 1}>
                      <TableCell mono className="font-semibold">
                        {domain.name}
                      </TableCell>
                      <TableCell>
                        {domain.publishedPolicy ? (
                          <PolicyBadge policy={domain.publishedPolicy as DmarcPolicy} />
                        ) : (
                          <span className="text-sm text-faint">Unknown</span>
                        )}
                      </TableCell>
                      <TableCell align="right">
                        <div className="flex items-center justify-end gap-2">
                          <ComplianceBar
                            value={domain.complianceRate * 100}
                            showValue={false}
                            width={96}
                            className="hidden sm:inline-flex"
                          />
                          <span className="font-mono text-sm">{formatPercent(domain.complianceRate)}</span>
                        </div>
                      </TableCell>
                      <TableCell>
                        <span className="flex items-center gap-2">
                          <Badge variant={status.badge}>{status.label}</Badge>
                          <Badge variant={enforcement.badge}>{enforcement.label}</Badge>
                        </span>
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          </div>
          {domains.length === 0 ? (
            <p className="px-5 py-10 text-center text-sm text-secondary">No domains are reporting yet.</p>
          ) : null}
        </Card>

        <p className="mt-4 text-xs text-faint">
          Shared read-only report. It shows this client only and stops working when the link expires or is
          revoked.
        </p>
      </div>
    </div>
  )
}
