import { describe, expect, it } from 'vitest'

import { addLateAlert } from './lateResults'
import type { InspectResult } from './types'

const result = (image_id: string, verdict: 'OK' | 'NG'): InspectResult => ({
  image_id,
  verdict,
  findings: '',
  confidence: 0.9,
  timestamp: '2026-10-22T00:00:00Z',
  late: true,
})

describe('addLateAlert', () => {
  it('늦은 NG 만 모은다', () => {
    let alerts = addLateAlert([], result('a', 'OK'))
    expect(alerts).toEqual([])
    alerts = addLateAlert(alerts, result('b', 'NG'))
    expect(alerts.map((a) => a.image_id)).toEqual(['b'])
  })

  it('같은 판정을 두 번 넣지 않는다', () => {
    const once = addLateAlert([], result('b', 'NG'))
    expect(addLateAlert(once, result('b', 'NG'))).toBe(once)
  })
})
