import { describe, expect, it } from 'vitest'
import { AUTHORITY_STAGES, evidenceSummary, needsAcknowledgement, nextStageUp, stageLabel, unverifiedMark } from './authority'

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

  it('신호의 재현율 항(오경보 고정 재현율)과 순위 품질을 서버가 준 대로 보이고, 점수가 없으면 지어내지 않는다', () => {
    const rows = evidenceSummary({ ml_recall_at_far: 0.9667, ml_auroc: 0.98123, ml_ranked_ng: 60, ml_ranked_ok: 120 }).rows
    expect(rows[3].value).toBe('96.7 % · 점수 표본 불량 60 / 정상 120')
    expect(rows[4].value).toBe('0.981')
    const none = evidenceSummary({ labeled_ng: 60 }).rows
    expect(none[3].value).toBe('— · 점수 표본 불량 0 / 정상 0')
    expect(none[4].value).toBe('—')
  })

  it('분모가 0 이면 비율을 지어내지 않는다', () => {
    const rows = evidenceSummary({ ml_ok_scored: 0, ml_false_alarm_rate: null }).rows
    expect(rows[2].value).toBe('0건 (—)')
  })
})

describe('needsAcknowledgement', () => {
  it('ML 이 판정 권한을 받는 격상은 신호가 «적격» 일 때만 확인 없이 간다', () => {
    expect(needsAcknowledgement('advisory', 'co_primary', 'eligible')).toBe(false)
    expect(needsAcknowledgement('advisory', 'co_primary', 'unmet')).toBe(true)
    expect(needsAcknowledgement('advisory', 'co_primary', 'insufficient')).toBe(true)
    expect(needsAcknowledgement('co_primary', 'ml_primary', null)).toBe(true) // 신호를 아직 못 읽었으면 «적격» 이 아니다
  })

  it('ML 이 판정 권한을 받지 않는 격상(shadow → advisory)은 묻지 않고, 단계를 건너뛰면 신호와 무관하게 묻는다', () => {
    expect(needsAcknowledgement('shadow', 'advisory', 'insufficient')).toBe(false)
    expect(needsAcknowledgement('advisory', 'ml_primary', 'eligible')).toBe(true)
  })
})

describe('unverifiedMark', () => {
  it('확인하고 올린 격상에만 표지와 사유를 사람 말로 보이고, 나머지는 표지가 없다', () => {
    expect(unverifiedMark({ acknowledged_unverified: true, unverified_basis: ['skipped_stage', 'signal_unmet'] }))
      .toBe('근거 없이 올림 — 단계를 건너뜀 · 격상 신호 «적격 아님»')
    expect(unverifiedMark({ acknowledged_unverified: true, unverified_basis: ['signal_insufficient'] })).toContain('표본 부족')
    expect(unverifiedMark({ acknowledged_unverified: true })).toBe('근거 없이 올림') // 사유 목록이 없어도 확인 사실은 보인다
    expect(unverifiedMark({ acknowledged_unverified: true, unverified_basis: ['future_reason'] })).toContain('future_reason') // 모르는 표지는 그대로
    expect(unverifiedMark({})).toBeNull() // 근거가 있었던 격상 · 격하 · 옛 이력
  })
})
