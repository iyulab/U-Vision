import { describe, expect, it } from 'vitest'

import { datasetNotice, formatPercent, fraction, hasNoMetricData, recallLead } from './metrics'
import type { MetricsSummary } from './types'

/** 최소 MetricsSummary 픽스처 — 필수 필드만 채움. */
function makeSummary(overrides: Partial<MetricsSummary>): MetricsSummary {
  return {
    scenario_id: 'demo',
    date: '2026-06-22',
    inspections: 0,
    ml_checked: 0,
    match_mismatches: 0,
    match_abstained: 0,
    dataset_inspections: 0,
    contested: 0,
    ml_degraded: 0,
    agreements: 0,
    reviews_required: 0,
    labeled: 0,
    labeled_ng: 0,
    vlm_ng_hits: 0,
    ml_ng_scored: 0,
    ml_ng_hits: 0,
    agreement_rate: null,
    review_rate: null,
    degrade_rate: null,
    vlm_ng_recall: null,
    ml_ng_recall: null,
    fail_closed: 0,
    fail_closed_rate: null,
    audited: 0,
    label_consistent: 0,
    label_conflicts_open: 0,
    label_consistency_rate: null,
    oracle_evaluated: 0,
    oracle_conflicts: 0,
    oracle_pending: 0,
    oracle_consistency_rate: null,
    oracle_coverage_rate: null,
    ...overrides,
  }
}

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

describe('hasNoMetricData', () => {
  it('inspections=0·fail_closed=0 → 데이터 없음(true)', () => {
    expect(hasNoMetricData(makeSummary({}))).toBe(true)
  })

  it('inspections=0·fail_closed=2 → 데이터 있음(false) — 회귀 케이스', () => {
    expect(hasNoMetricData(makeSummary({ fail_closed: 2 }))).toBe(false)
  })

  it('inspections=3·fail_closed=0 → 데이터 있음(false)', () => {
    expect(hasNoMetricData(makeSummary({ inspections: 3 }))).toBe(false)
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

describe('datasetNotice', () => {
  it('주입 판정이 없으면 아무것도 말하지 않는다', () => {
    expect(datasetNotice({ inspections: 5, dataset_inspections: 0 })).toBeNull()
  })
  it('전부 주입이면 현장 수치가 아니라고, 일부면 섞여 있다고 말한다', () => {
    expect(datasetNotice({ inspections: 63, dataset_inspections: 63 })).toContain('모두')
    expect(datasetNotice({ inspections: 10, dataset_inspections: 3 })).toContain('10건 가운데 3건')
  })
})
