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
 * VLM·ML NG recall 비교 결과 — 대시보드가 우열을 강조(전용 ML 이 VLM 보다 NG 를 잘 잡는지 운영 데이터로 확인).
 * 둘 중 하나라도 측정 불가(null)면 비교 안 함('none').
 */
export type RecallLead = 'ml' | 'vlm' | 'tie' | 'none'

export function recallLead(vlm: number | null, ml: number | null): RecallLead {
  if (vlm === null || ml === null) return 'none'
  if (ml > vlm) return 'ml'
  if (vlm > ml) return 'vlm'
  return 'tie'
}

/**
 * 시험 데이터셋에서 넣은 판정이 집계에 있으면 그 사실 한 줄 — 없으면 null. 전부 주입이면 현장 수치가 아니라는 것을,
 * 일부면 섞여 있다는 것을 말한다.
 */
export function datasetNotice(s: Pick<MetricsSummary, 'inspections' | 'dataset_inspections'>): string | null {
  if (s.dataset_inspections <= 0) return null
  if (s.dataset_inspections >= s.inspections)
    return `검사 ${s.inspections}건 모두 시험 데이터셋에서 넣은 판정입니다 — 현장 촬영 수치가 아닙니다.`
  return `검사 ${s.inspections}건 가운데 ${s.dataset_inspections}건은 시험 데이터셋에서 넣은 판정입니다 — 아래 수치는 현장 촬영과 섞여 있습니다.`
}
