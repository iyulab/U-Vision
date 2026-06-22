import { describe, expect, it } from 'vitest'

import { formatPercent, fraction, recallLead } from './metrics'

describe('formatPercent', () => {
  it('비율을 퍼센트로 — 0자리 기본', () => {
    expect(formatPercent(0.6667)).toBe('67%')
    expect(formatPercent(1)).toBe('100%')
    expect(formatPercent(0)).toBe('0%')
  })

  it('null/NaN 은 데이터 없음(—) — 0% 위장 금지', () => {
    expect(formatPercent(null)).toBe('—')
    expect(formatPercent(Number.NaN)).toBe('—')
  })

  it('자리수 지정', () => {
    expect(formatPercent(0.6667, 1)).toBe('66.7%')
  })
})

describe('fraction', () => {
  it('n/d 표기', () => {
    expect(fraction(2, 3)).toBe('2/3')
  })
})

describe('recallLead', () => {
  it('ML 우세/VLM 우세/동률', () => {
    expect(recallLead(0.8, 0.9)).toBe('ml')
    expect(recallLead(0.9, 0.8)).toBe('vlm')
    expect(recallLead(0.8, 0.8)).toBe('tie')
  })

  it('한쪽이라도 측정 불가면 비교 안 함', () => {
    expect(recallLead(null, 0.9)).toBe('none')
    expect(recallLead(0.9, null)).toBe('none')
    expect(recallLead(null, null)).toBe('none')
  })
})
