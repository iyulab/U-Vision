import { describe, expect, it } from 'vitest'
import { AUTHORITY_STAGES, evidenceSummary, nextStageUp, stageLabel } from './authority'

describe('authority stages', () => {
  it('orders stages', () => {
    expect(AUTHORITY_STAGES).toEqual(['shadow', 'advisory', 'co_primary', 'ml_primary'])
  })
  it('nextStageUp returns the stage above', () => {
    expect(nextStageUp('advisory')).toBe('co_primary')
    expect(nextStageUp('ml_primary')).toBeNull() // 최상위
  })
  it('stageLabel is human-readable korean', () => {
    expect(stageLabel('co_primary')).toContain('동등')
  })
})

describe('evidenceSummary', () => {
  it('서버가 준 값만 옮기고 신호를 세 갈래로 가른다', () => {
    const base = { labeled_ng: 60, labeled_ok: 120, ml_ng_scored: 60, ml_ng_hits: 59, ml_ng_recall: 0.983, ml_ok_scored: 120, ml_false_alarms: 6, ml_false_alarm_rate: 0.05 }
    expect(evidenceSummary({ ...base, promotion_eligible: true }).signal).toBe('eligible')
    expect(evidenceSummary({ ...base, promotion_eligible: false }).signal).toBe('unmet')
    expect(evidenceSummary({ ...base, promotion_eligible: null }).signal).toBe('insufficient')
    expect(evidenceSummary({}).signal).toBe('insufficient')
    const rows = evidenceSummary(base).rows
    expect(rows[0].value).toBe('60 / 120')
    expect(rows[2].value).toBe('6/120 (5.0 %)')
  })

  it('분모가 0 이면 비율을 지어내지 않는다', () => {
    const rows = evidenceSummary({ ml_ok_scored: 0, ml_false_alarm_rate: null }).rows
    expect(rows[2].value).toBe('0건 (—)')
  })
})
