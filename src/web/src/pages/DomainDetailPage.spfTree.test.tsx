import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { SpfDependencyNode, SpfTerm } from '@/lib/analytics'
import { SpfDependencyTree } from '@/pages/DomainDetailPage'

function term(overrides: Partial<SpfTerm> & { text: string }): SpfTerm {
  return {
    kind: 'include',
    qualifier: '+',
    target: null,
    costsLookup: true,
    isDynamic: false,
    note: null,
    resolution: null,
    mxHosts: [],
    mxHostTotal: 0,
    ...overrides,
  }
}

function node(overrides: Partial<SpfDependencyNode> & { domain: string }): SpfDependencyNode {
  return {
    depth: 0,
    status: 'found',
    raw: null,
    terms: [],
    lookupsUsed: 0,
    issues: [],
    ...overrides,
  }
}

describe('SpfDependencyTree', () => {
  it('renders nested resolutions with their lookup counts', () => {
    render(
      <SpfDependencyTree
        node={node({
          domain: 'acme.example',
          lookupsUsed: 2,
          terms: [
            term({
              text: 'include:_spf.example.com',
              target: '_spf.example.com',
              resolution: node({
                domain: '_spf.example.com',
                depth: 1,
                lookupsUsed: 1,
                terms: [term({ text: 'mx', kind: 'mx', mxHosts: ['mail.example.com'], mxHostTotal: 1 })],
              }),
            }),
            term({ text: '-all', kind: 'all', qualifier: '-', costsLookup: false }),
          ],
        })}
      />,
    )

    expect(screen.getByText('acme.example')).toBeInTheDocument()
    expect(screen.getByText('_spf.example.com')).toBeInTheDocument()
    expect(screen.getByText('include:_spf.example.com')).toBeInTheDocument()
    expect(screen.getByText('2 lookups')).toBeInTheDocument()
    expect(screen.getByText(/mx: mail\.example\.com/)).toBeInTheDocument()
  })

  it('marks dynamic terms and shows follow notes', () => {
    render(
      <SpfDependencyTree
        node={node({
          domain: 'acme.example',
          lookupsUsed: 1,
          terms: [
            term({
              text: 'include:%{i}._spf.example.com',
              target: '%{i}._spf.example.com',
              isDynamic: true,
              note: 'Macro in the target — unexpandable without a sender identity, not followed.',
            }),
          ],
        })}
      />,
    )

    expect(screen.getByText('dynamic')).toBeInTheDocument()
    expect(screen.getByText(/unexpandable without a sender identity/)).toBeInTheDocument()
  })

  it('badges non-found nodes and lists node issues', () => {
    render(
      <SpfDependencyTree
        node={node({
          domain: 'acme.example',
          lookupsUsed: 1,
          terms: [
            term({
              text: 'include:ghost.example.com',
              target: 'ghost.example.com',
              resolution: node({
                domain: 'ghost.example.com',
                depth: 1,
                status: 'missing',
                issues: ['ghost.example.com publishes no SPF record.'],
              }),
            }),
          ],
        })}
      />,
    )

    expect(screen.getByText('no record')).toBeInTheDocument()
    expect(screen.getByText('ghost.example.com publishes no SPF record.')).toBeInTheDocument()
  })

  it('dedupes identical term text with a stable key', () => {
    render(
      <SpfDependencyTree
        node={node({
          domain: 'acme.example',
          lookupsUsed: 2,
          terms: [
            term({ text: 'include:dup.example.com', target: 'dup.example.com' }),
            term({ text: 'include:dup.example.com', target: 'dup.example.com' }),
          ],
        })}
      />,
    )

    expect(screen.getAllByText('include:dup.example.com')).toHaveLength(2)
  })
})
