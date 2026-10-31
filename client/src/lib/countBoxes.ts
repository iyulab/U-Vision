import type { CountInfo } from './types'

/** 서버 `CountEvaluator` 의 «문턱 근처» 띠 폭과 같다 — 이 안의 상자는 있을 수도 없을 수도 있는 상자다. */
export const NEAR_THRESHOLD_BAND = 0.1

/** 사진 위에 그릴 상자 하나 — 위치 · 크기는 이미지 표시 영역에 대한 퍼센트(좌표가 이미지 비율이라 크기를 몰라도 된다). */
export interface BoxStyle {
  left: number
  top: number
  width: number
  height: number
  label: string
  /** 신뢰도 0~1. */
  confidence: number
  /** 문턱 근처 — 넣고 빼는 데 따라 결론이 바뀔 수 있는 상자. */
  near: boolean
}

const clamp = (v: number) => Math.min(100, Math.max(0, v * 100))

/**
 * 세지 않은 문턱 근처 상자 → 그릴 사각형(점선). 세지 않았으므로 `near` 는 언제나 참이다 — 넣었다면 개수가 달라졌을 상자라
 * 확인하는 사람이 사진에서 이것을 찾는다.
 */
export function nearBoxStyles(count: Pick<CountInfo, 'near_boxes'>): BoxStyle[] {
  return (count.near_boxes ?? []).map((b) => ({
    left: clamp(b.x),
    top: clamp(b.y),
    width: clamp(b.w),
    height: clamp(b.h),
    label: b.class,
    confidence: b.confidence,
    near: true,
  }))
}

/** 근거 상자 → 그릴 사각형. 이미지 밖으로 나간 값은 0~100 으로 자른다. */
export function boxStyles(count: Pick<CountInfo, 'boxes' | 'min_confidence'>): BoxStyle[] {
  return count.boxes.map((b) => ({
    left: clamp(b.x),
    top: clamp(b.y),
    width: clamp(b.w),
    height: clamp(b.h),
    label: b.class,
    confidence: b.confidence,
    near: Math.abs(b.confidence - count.min_confidence) <= NEAR_THRESHOLD_BAND,
  }))
}

/** 센 결과 한 줄 — «센 개수 5 (기대 6 ± 1)» + 종류별. 구성 확인(항목 목록이 있는 판정)이면 «구성 확인 — 센 개수 5 (기대 합계 6)». */
export function countSummary(count: Pick<CountInfo, 'total' | 'expected' | 'tolerance' | 'by_class' | 'items'>): string {
  if (count.items) return `구성 확인 — 센 개수 ${count.total} (기대 합계 ${count.expected})`
  const kinds = Object.entries(count.by_class)
  const split = kinds.length > 1 ? ` — ${kinds.map(([k, n]) => `${k} ${n}`).join(' · ')}` : ''
  return `센 개수 ${count.total} (기대 ${count.expected} ± ${count.tolerance})${split}`
}

/** 항목 상태의 글자 — 색에만 기대지 않고 글자를 함께 보인다. 모르는 상태(서버가 더 지은 것)는 그대로 보인다. */
export function itemStatusLabel(status: string): string {
  return status === 'ok' ? '맞음' : status === 'missing' ? '부족' : status === 'extra' ? '초과' : status
}

/** 과잉 처리 문구 — 결과 화면이 «왜 이 판정인가» 를 읽게 한다. */
export function extraPolicyNote(policy: string | undefined): string {
  return policy === 'warn'
    ? '초과는 항목에 올리지만 판정에는 반영하지 않는다'
    : policy === 'ignore'
      ? '초과는 보지 않는다'
      : '초과도 불량으로 본다'
}
