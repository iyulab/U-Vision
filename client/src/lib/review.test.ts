import { describe, expect, it } from 'vitest'

import { labelMapOf } from './labels'
import { pendingReviewCount, reviewStateOf } from './review'
import type { StoredLabel, StoredResult } from './types'

function result(over: Partial<StoredResult>): StoredResult {
  return {
    scenario_id: 's',
    image_id: 'i',
    verdict: 'OK',
    findings: '',
    confidence: 0.9,
    timestamp: 't',
    image_file: 'i.jpg',
    device_id: '',
    device_label: '',
    ...over,
  }
}

describe('review', () => {
  it("reviewStateOf is 'auto' when ML inactive (requires_review absent)", () => {
    expect(reviewStateOf(result({}), undefined)).toBe('auto')
  })

  it("reviewStateOf is 'auto' when dual-check did not flag review", () => {
    expect(reviewStateOf(result({ requires_review: false }), undefined)).toBe('auto')
  })

  it("reviewStateOf is 'pending' when flagged and not yet labeled", () => {
    const r = result({ image_id: 'a', requires_review: true })
    expect(reviewStateOf(r, undefined)).toBe('pending')
  })

  it("reviewStateOf is 'reviewed' once a human label resolves it", () => {
    const r = result({ image_id: 'a', requires_review: true })
    const label: StoredLabel = { image_id: 'a', label: 'NG', timestamp: 't' }
    expect(reviewStateOf(r, label)).toBe('reviewed')
  })

  it('pendingReviewCount counts only flagged-and-unlabeled rows', () => {
    const results = [
      result({ image_id: 'a', requires_review: true }), // pending
      result({ image_id: 'b', requires_review: true }), // reviewed (labeled below)
      result({ image_id: 'c', requires_review: false }), // auto
      result({ image_id: 'd' }), // auto (ML inactive)
    ]
    const labels = labelMapOf([{ image_id: 'b', label: 'OK', timestamp: 't' }])
    expect(pendingReviewCount(results, labels)).toBe(1)
  })

  it('pendingReviewCount is 0 when nothing is flagged', () => {
    const results = [result({ image_id: 'a' }), result({ image_id: 'b' })]
    expect(pendingReviewCount(results, labelMapOf([]))).toBe(0)
  })

  it('oracle-only sidecar(operative 라벨 없음)는 여전히 pending', () => {
    const r = result({ image_id: 'a', requires_review: true })
    const oracleOnly: StoredLabel = {
      image_id: 'a',
      timestamp: 't',
      history: [{ label: 'NG', by: 'oracle', at: 't', mode: 'oracle' }],
    }
    expect(reviewStateOf(r, oracleOnly)).toBe('pending')
  })
})
