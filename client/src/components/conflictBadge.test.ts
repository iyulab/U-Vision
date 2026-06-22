import { describe, expect, it } from 'vitest'
import { conflictBadgeLabel } from './conflictBadge'
import type { StoredLabel } from '../lib/types'

function lbl(status: StoredLabel['audit']): StoredLabel {
  return { image_id: 'i', label: 'NG', timestamp: 't', audit: status }
}

describe('conflictBadgeLabel', () => {
  it('conflicted → 배지 문구', () => {
    expect(conflictBadgeLabel(lbl({ status: 'conflicted' }))).toBe('⚠ 충돌')
  })
  it('그 외 → null', () => {
    expect(conflictBadgeLabel(lbl({ status: 'resolved' }))).toBeNull()
    expect(conflictBadgeLabel(undefined)).toBeNull()
  })
})
