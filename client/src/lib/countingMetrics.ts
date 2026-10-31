import { formatPercent, fraction } from './metrics'
import type { CountingMetrics } from './types'

/** 구성 확인의 종류 한 줄 — 그 종류가 모자랐던 건수와 과잉이었던 건수. */
export interface KindRow {
  kind: string
  missing: number
  extra: number
}

/**
 * 종류별 누락/과잉 빈도 표의 줄 — 두 사전의 종류를 합치고, 어긋난 건수가 많은 종류를 위로 둔다(같으면 이름 순). 어긋난 종류가 하나도
 * 없으면 빈 배열이다. 정렬 키가 이름까지 가므로 서버가 사전 순서를 바꿔도 화면은 흔들리지 않는다.
 */
export function kindRows(c: Pick<CountingMetrics, 'missing_by_kind' | 'extra_by_kind'>): KindRow[] {
  const kinds = new Set([...Object.keys(c.missing_by_kind), ...Object.keys(c.extra_by_kind)])
  return [...kinds]
    .map((kind) => ({ kind, missing: c.missing_by_kind[kind] ?? 0, extra: c.extra_by_kind[kind] ?? 0 }))
    .sort((a, b) => b.missing + b.extra - (a.missing + a.extra) || (a.kind < b.kind ? -1 : a.kind > b.kind ? 1 : 0))
}

/**
 * 시험 데이터셋에서 넣은 판정이 계수 집계에 있으면 그 사실 한 줄 — 없으면 null. 전부 주입이면 현장 수치가 아니라는 것을, 일부면
 * 섞여 있다는 것을 말한다(VLM · ML 집계의 알림과 같은 규칙).
 */
export function countingDatasetNotice(c: Pick<CountingMetrics, 'checked' | 'dataset_checked'>): string | null {
  if (c.dataset_checked <= 0) return null
  if (c.dataset_checked >= c.checked)
    return `계수 판정 ${c.checked}건 모두 시험 데이터셋에서 넣은 판정입니다 — 현장 촬영 수치가 아닙니다.`
  return `계수 판정 ${c.checked}건 가운데 ${c.dataset_checked}건은 시험 데이터셋에서 넣은 판정입니다 — 아래 수치는 현장 촬영과 섞여 있습니다.`
}

/**
 * 구성 확인(종류별 기대) 한 줄 — 구성 확인이 아니면(총 개수 기대뿐이면) null. 분모는 구성 확인 건이고, 모자란 종류가 있던 건과
 * 과잉이 있던 건은 겹칠 수 있다(한 사진에 둘 다) — 합이 분모와 같지 않다.
 */
export function compositionLine(
  c: Pick<CountingMetrics, 'composition_checked' | 'with_missing' | 'with_extra' | 'missing_rate' | 'extra_rate'>,
): string | null {
  if (c.composition_checked <= 0) return null
  return (
    `구성 확인 ${c.composition_checked}건 — 모자란 종류가 있던 건 ${fraction(c.with_missing, c.composition_checked)} (${formatPercent(c.missing_rate)})` +
    ` · 과잉이 있던 건 ${fraction(c.with_extra, c.composition_checked)} (${formatPercent(c.extra_rate)})`
  )
}

/**
 * 판정하지 못한 시도 한 줄 — 없으면 null. 검출기가 꺼졌거나 답하지 못해 개수를 모르는 채 OK 를 내지 않은 건이다(VLM 판정 불가와
 * 다르다).
 */
export function unavailableNote(c: Pick<CountingMetrics, 'unavailable'>): string | null {
  if (c.unavailable <= 0) return null
  return `검출기가 꺼졌거나 답하지 못해 판정하지 못한 시도가 ${c.unavailable}건 있습니다 — 개수를 모르는 채 통과시키지 않았습니다.`
}
