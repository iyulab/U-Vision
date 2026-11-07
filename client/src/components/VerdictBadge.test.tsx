import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import type { InspectResult } from '../lib/types'
import { VerdictBadge } from './VerdictBadge'

const result = (over: Partial<InspectResult>): InspectResult => ({
  verdict: 'OK',
  findings: '',
  confidence: 0.82,
  timestamp: '2026-11-07T01:00:00Z',
  image_id: 'img-1',
  ...over,
})

// vitest 전역을 켜지 않아 RTL 의 자동 정리가 걸리지 않는다.
afterEach(cleanup)

describe('VerdictBadge 소견', () => {
  it('NG 의 소견을 보인다', () => {
    render(<VerdictBadge result={result({ verdict: 'NG', findings: '표면에 긁힘이 보인다' })} phase="done" />)
    expect(screen.getByText('표면에 긁힘이 보인다')).toBeTruthy()
  })

  it('OK 의 소견도 숨기지 않는다 — 판정의 근거이고, 비어 있으면 아무것도 그리지 않는다', () => {
    const { unmount } = render(<VerdictBadge result={result({ findings: '표면이 깨끗하다' })} phase="done" />)
    expect(screen.getByText('표면이 깨끗하다')).toBeTruthy()
    unmount()

    render(<VerdictBadge result={result({ findings: '' })} phase="done" />)
    expect(screen.queryByText('표면이 깨끗하다')).toBeNull()
    expect(screen.getByText(/신뢰도 82%/)).toBeTruthy()
  })
})
