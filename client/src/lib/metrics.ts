/**
 * 메트릭 표시 순수 로직(B3 대시보드) — 비율 포맷·NG recall 비교·empty-state 판정.
 * 분모 0(null) 을 "0%" 로 위장하지 않고 "—"(데이터 없음)로 정직히 표시한다.
 */

import type { MetricsSummary } from './types'

/** 비율(0~1, null=undefined) → 퍼센트 문자열. null 이면 '—'(데이터 없음). */
export function formatPercent(rate: number | null, digits = 0): string {
  if (rate === null || Number.isNaN(rate)) return '—'
  return `${(rate * 100).toFixed(digits)}%`
}

/** 분자/분모 카운트 → "n/d" 보조 표기. */
export function fraction(numerator: number, denominator: number): string {
  return `${numerator}/${denominator}`
}

/** 메트릭 데이터(2중체크 또는 fail-closed)가 한 건도 없는가 — 대시보드 empty-state 판정. */
export function hasNoMetricData(summary: MetricsSummary): boolean {
  return summary.inspections + summary.fail_closed === 0
}

/**
 * VLM·ML NG recall 비교 결과 — 대시보드가 우열을 강조(FW-3 "ML>VLM" 이 운영서 유지되는지).
 * 둘 중 하나라도 측정 불가(null)면 비교 안 함('none').
 */
export type RecallLead = 'ml' | 'vlm' | 'tie' | 'none'

export function recallLead(vlm: number | null, ml: number | null): RecallLead {
  if (vlm === null || ml === null) return 'none'
  if (ml > vlm) return 'ml'
  if (vlm > ml) return 'vlm'
  return 'tie'
}
