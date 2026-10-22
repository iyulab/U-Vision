import { agreementOf, decidedClassOf, operativeLabel } from './labels'
import type { StoredLabel, StoredResult } from './types'

/** 하루치 검사 결과의 요약 — 결과 조회 화면 머리에 보인다. */
export interface ResultSummary {
  total: number
  ok: number
  ng: number
  /** NG 비율(0~1). 기록이 없으면 null. */
  ngRate: number | null
  /** 사람 라벨이 붙은 건수. */
  labeled: number
  /** 사람 라벨이 판정과 다른 건수 — 판정이 틀렸다고 사람이 고친 것. */
  corrected: number
  /** NG 의 불량 유형 분포(많은 순) — 시나리오가 유형을 나눌 때만 채워진다. */
  defects: { id: string; count: number }[]
}

export function summarizeResults(results: StoredResult[], labels: Map<string, StoredLabel>): ResultSummary {
  let ok = 0
  let ng = 0
  let labeled = 0
  let corrected = 0
  const defectCounts = new Map<string, number>()
  for (const r of results) {
    if (r.verdict === 'OK') ok++
    else if (r.verdict === 'NG') {
      ng++
      if (r.class !== undefined) defectCounts.set(r.class, (defectCounts.get(r.class) ?? 0) + 1)
    }
    const agreement = agreementOf(decidedClassOf(r), operativeLabel(labels.get(r.image_id)))
    if (agreement !== 'unlabeled') labeled++
    if (agreement === 'mismatch') corrected++
  }
  const defects = [...defectCounts].map(([id, count]) => ({ id, count })).sort((a, b) => b.count - a.count)
  return { total: results.length, ok, ng, ngRate: results.length ? ng / results.length : null, labeled, corrected, defects }
}

/** 비율을 «12.5%» 로(소수 한 자리, 정수면 소수 없음). */
export function percent(rate: number): string {
  const v = Math.round(rate * 1000) / 10
  return `${Number.isInteger(v) ? v.toFixed(0) : v.toFixed(1)}%`
}
