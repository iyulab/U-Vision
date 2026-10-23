import { describe, expect, it } from 'vitest'
import { reviewBandText } from './verdictBadgeText'
import type { InspectResult } from '../lib/types'

function r(over: Partial<InspectResult>): InspectResult {
  return { verdict: 'OK', findings: '', confidence: 0.9, timestamp: 't', image_id: 'i', ...over }
}

describe('reviewBandText', () => {
  it('requires_review=true + ML 불일치 → 검토 + ML 의견', () => {
    expect(reviewBandText(r({ requires_review: true, ml: { label: 'ng', confidence: 0.8 }, agreement: false })))
      .toBe('🔍 검토 필요 · ML 의견: NG')
  })
  it('대조 단과 맞섬 → 검토 + 읽은 코드가 다르다는 사실', () => {
    expect(reviewBandText(r({ requires_review: true, contested_by: [{ tier: 'match', class: 'MISMATCH' }] })))
      .toBe('🔍 검토 필요 · 읽은 코드가 기대 품번과 다릅니다')
  })
  it('requires_review=true, ML 없음 → 검토만', () => {
    expect(reviewBandText(r({ requires_review: true }))).toBe('🔍 검토 필요')
  })
  it('requires_review 아님 → null', () => {
    expect(reviewBandText(r({ requires_review: false }))).toBeNull()
    expect(reviewBandText(r({}))).toBeNull()
  })
})
