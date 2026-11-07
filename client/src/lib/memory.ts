/**
 * 확정 사례 기억 표시 순수 로직 — 서버 `GET /memory` 의 상태 한 줄과 문턱 재생 행.
 * 기억은 아직 판정을 바꾸지 않는다(관찰 전용) — 재생 표는 «언제 맡겨도 되나»를 읽는 자리다.
 */

import { formatPercent } from './metrics'
import type { MemoryNeighborCurve, MemoryRecommendation, MemoryReplayRow, MemoryReport, Scenario, ScenarioInput } from './types'

/** 상태 한 줄 — 지어졌으면 사례 수 · 인코더 판 · 지은 시각, 아니면 왜 비었는지. */
export function memoryStatusLine(report: MemoryReport, now: Date = new Date()): string {
  if (report.encoder === undefined || report.cases === undefined) {
    return '아직 지어지지 않았습니다 — 사람이 라벨을 확정하면 다음 동기화에 들어갑니다.'
  }
  const built = report.built_at ? ` · ${ago(new Date(report.built_at), now)} 갱신` : ''
  return `확정 사례 ${report.cases}건 · 인코더 ${report.encoder}${built}`
}

/** 문턱 표기 — 코사인 거리 그대로(소수 두 자리). */
export function formatDistance(d: number): string {
  return d.toFixed(2)
}

/** 놓침이 있는 행인가 — 기억이 OK 로 답했는데 사람이 불량이라 한 건. 그 문턱에서는 기억에 맡기면 안 된다. */
export function hasMiss(row: MemoryReplayRow): boolean {
  return row.missed > 0
}

function ago(then: Date, now: Date): string {
  const s = Math.max(0, Math.round((now.getTime() - then.getTime()) / 1000))
  if (s < 60) return `${s}초 전`
  const m = Math.round(s / 60)
  if (m < 60) return `${m}분 전`
  const h = Math.round(m / 60)
  if (h < 48) return `${h}시간 전`
  return `${Math.round(h / 24)}일 전`
}

/** 재생 창 선택 — 서버 `days`(1~90). 하루가 기본이고 길수록 불량 라벨이 모여 추천의 증거가 단단해진다. */
export const MEMORY_WINDOWS: readonly { days: number; label: string }[] = [
  { days: 1, label: '하루' },
  { days: 7, label: '7일' },
  { days: 30, label: '30일' },
]

export type RecommendationTone = 'none' | 'candidate' | 'sufficient'

export interface RecommendationView {
  tone: RecommendationTone
  headline: string
  detail: string
}

/**
 * 문턱 추천 표시 — 추천은 제안이다. 놓침 0 은 증거가 아니라 관찰이라(불량 6장이면 통과율이 39 % 까지 가능) 상한이 목표 안일
 * 때만 «추천», 아니면 «후보» 로 말하고 목표를 주장하는 데 필요한 불량 라벨 수를 알린다. 근거가 없으면 이유만.
 */
export function recommendationView(rec: MemoryRecommendation): RecommendationView {
  if (rec.max_distance === undefined) {
    return { tone: 'none', headline: '추천할 근거가 아직 없습니다', detail: rec.reason }
  }
  const evidence = `불량 ${rec.labeled_defects}장 중 통과 ${rec.missed} · 통과율 95% 상한 ${formatPercent(rec.defect_pass_upper_95 ?? null)}`
  const target = formatPercent(rec.target_miss_rate)
  if (rec.sufficient) {
    return { tone: 'sufficient', headline: `추천 문턱 ${formatDistance(rec.max_distance)}`, detail: `${evidence} — 목표 ${target} 안` }
  }
  return {
    tone: 'candidate',
    headline: `후보 문턱 ${formatDistance(rec.max_distance)}`,
    detail: `${evidence} — 목표 ${target} 를 주장하려면 놓침 0 으로 불량 라벨 ${rec.defects_needed}장이 필요합니다. 아직 후보일 뿐입니다.`,
  }
}

export interface NeighborCurveRowView {
  k: number
  configured: boolean
  /** 답한 비율과 («답함/센 수»). */
  answer: string
  /** 불량 통과 — 불량 라벨이 없으면 비율 대신 그렇게 말한다(0 으로 꾸미지 않는다). */
  defectPass: string
  /** 불량이 OK 로 지나갔나 — 강조. */
  hasMiss: boolean
}

export interface NeighborCurveView {
  rows: NeighborCurveRowView[]
  caption: string
  /** 불량 라벨이 증거로 모자란가 — 한 장의 차이가 큰 표임을 알린다. */
  thin: boolean
  /** 모자란 표에 붙는 경고(없으면 null). */
  warning: string | null
}

/**
 * 이웃 수 k 곡선 표시 — 서버가 k 를 고르지 않으므로 이 표도 고르지 않는다. 응답률과 불량 통과를 «나란히» 보이고, 불량 라벨이
 * 목표를 주장할 만큼(`defectsNeeded`, 서버 추천의 증거 기준) 되지 않으면 한 장의 차이가 큰 표임을 알린다. 불량 라벨이 없는
 * 행은 통과를 «측정 불가» 로 말한다.
 */
export function neighborCurveView(curve: MemoryNeighborCurve, defectsNeeded: number): NeighborCurveView {
  const rows = curve.rows.map((r): NeighborCurveRowView => ({
    k: r.k,
    configured: r.configured,
    answer: `${formatPercent(r.answer_rate)} (${r.answered}/${r.counted})`,
    defectPass:
      r.labeled_defects === 0
        ? '불량 라벨 없음 — 측정 불가'
        : `${r.missed}/${r.labeled_defects} (${formatPercent(r.defect_pass_rate ?? null)})`,
    hasMiss: r.missed > 0,
  }))
  const smallest = curve.rows.reduce((m, r) => Math.min(m, r.labeled_defects), Number.POSITIVE_INFINITY)
  const thin = curve.rows.length > 0 && smallest < defectsNeeded
  return {
    rows,
    thin,
    caption: `이웃 수 k — 거리 문턱 ${formatDistance(curve.max_distance)} 그대로 k 만 바꿨다면. k 가 클수록 OK 로 지나가는 불량은 줄지만 기억이 답하는 비율도 함께 떨어집니다. 서버는 k 를 고르지 않습니다 — 두 값을 함께 보고 정하세요(이웃 수는 설치 설정입니다).`,
    warning: thin ? `불량 라벨이 ${smallest === Number.POSITIVE_INFINITY ? 0 : smallest}장뿐이라 한 장의 차이가 통과율에 크게 보입니다 — 목표를 주장하려면 놓침 0 으로 ${defectsNeeded}장이 필요합니다.` : null,
  }
}

/** 적용 단추를 보일 수 있나 — 추천 문턱이 있고 지금 설정값과 다를 때. */
export function canApplyRecommendation(rec: MemoryRecommendation, configured: number | undefined): boolean {
  if (rec.max_distance === undefined) return false
  return configured === undefined || Math.abs(rec.max_distance - configured) > 1e-9
}

/**
 * 문턱만 바꾸는 시나리오 수정 본문. 서버 PUT 은 v1 필드(이름 · 기준 · 캡처 설정)를 요청에서 그대로 받으므로
 * 저장된 값을 모두 실어 보낸다 — 문턱 하나를 적용하다 다른 설정이 기본값으로 돌아가지 않게. 유형 · 불량 유형 · 계수 필드 ·
 * NG 유형 설명 · 클라우드 송출 허용은 싣지 않는다(생략하면 서버가 저장된 값을 보존한다 — 허용은 보이는 토글로만 바꾼다).
 */
export function distanceUpdateInput(scenario: Scenario, distance: number): ScenarioInput {
  return {
    name: scenario.name,
    criteria: scenario.criteria,
    roi: scenario.roi,
    motion_threshold: scenario.motion_threshold,
    still_frames: scenario.still_frames,
    min_sharpness: scenario.min_sharpness,
    max_image_dimension: scenario.max_image_dimension,
    reference_cap: scenario.reference_cap,
    memory_max_distance: distance,
  }
}
