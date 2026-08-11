import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { DispositionBadges } from '@/pages/DomainDetailPage'

/**
 * The breakdown has to account for every message the panel above it claims exists. Before
 * RFC 9990's `pass` had a bucket, an all-pass source rendered three zeroes next to a
 * non-zero total — so these assert the fourth badge is present and carries the count,
 * rather than that the row merely renders.
 */
describe('DispositionBadges', () => {
  it('renders a bucket for each of the four action dispositions', () => {
    render(<DispositionBadges dispositions={{ none: 1, pass: 2, quarantine: 4, reject: 8 }} />)

    expect(screen.getByText('none · 1')).toBeInTheDocument()
    expect(screen.getByText('pass · 2')).toBeInTheDocument()
    expect(screen.getByText('quarantine · 4')).toBeInTheDocument()
    expect(screen.getByText('reject · 8')).toBeInTheDocument()
  })

  it('shows the pass count for an all-pass source instead of three zeroes', () => {
    render(<DispositionBadges dispositions={{ none: 0, pass: 7, quarantine: 0, reject: 0 }} />)

    expect(screen.getByText('pass · 7')).toBeInTheDocument()
    expect(screen.getByText('none · 0')).toBeInTheDocument()
  })
})
