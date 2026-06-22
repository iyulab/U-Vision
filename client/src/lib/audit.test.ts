import { describe, expect, it } from 'vitest'
import { auditStatusOf, conflictCount, isConflicted } from './audit'
import type { StoredLabel } from './types'

function lbl(over: Partial<StoredLabel>): StoredLabel {
  return { image_id: 'i', label: 'NG', timestamp: 't', ...over }
}

describe('audit helpers', () => {
  it('auditStatusOf — 없으면 unaudited', () => {
    expect(auditStatusOf(undefined)).toBe('unaudited')
    expect(auditStatusOf(lbl({}))).toBe('unaudited')
    expect(auditStatusOf(lbl({ audit: { status: 'conflicted' } }))).toBe('conflicted')
  })

  it('isConflicted — conflicted 만 true', () => {
    expect(isConflicted(lbl({ audit: { status: 'conflicted' } }))).toBe(true)
    expect(isConflicted(lbl({ audit: { status: 'resolved' } }))).toBe(false)
    expect(isConflicted(undefined)).toBe(false)
  })

  it('conflictCount — 미해소 충돌만 센다', () => {
    expect(conflictCount([
      lbl({ image_id: 'a', audit: { status: 'conflicted' } }),
      lbl({ image_id: 'b', audit: { status: 'resolved' } }),
      lbl({ image_id: 'c', audit: { status: 'consistent' } }),
    ])).toBe(1)
  })
})
