/**
 * 플라이휠 곡선 표시 순수 로직 — 서버 `GET /flywheel` 의 구간 점을 표 · 추세 문구 · 선 그래프 좌표로.
 * 곡선은 «쓸수록 나아진다» 의 증거다 — 구간이 작으면 비율이 흔들리므로 판단할 만한 구간이 모자라면 추세를 말하지 않는다.
 */

import { formatPercent } from './metrics'
import type { FlywheelPoint } from './types'

/** 추세를 말하는 데 필요한 구간당 최소 판정 수 — 이보다 작은 구간의 비율은 흔들려 추세에 넣지 않는다. */
export const MIN_JUDGED = 10

/** 앞 절반과 뒤 절반의 평균 차가 이만큼(비율 5 %p)이면 오름 · 내림, 안이면 평평. */
const FLAT_BAND = 0.05

export interface Trend {
  kind: 'up' | 'flat' | 'down'
  from: number
  to: number
}

const round4 = (x: number) => Math.round(x * 1e4) / 1e4
const mean = (xs: number[]) => xs.reduce((a, b) => a + b, 0) / xs.length

/** 값의 추세 — 앞 절반 평균 대 뒤 절반 평균. 값이 둘 미만이면 판단하지 않는다(null). */
export function trendOf(values: number[]): Trend | null {
  if (values.length < 2) return null
  const half = Math.floor(values.length / 2)
  const from = round4(mean(values.slice(0, half)))
  const to = round4(mean(values.slice(values.length - half)))
  const delta = to - from
  return { kind: delta > FLAT_BAND ? 'up' : delta < -FLAT_BAND ? 'down' : 'flat', from, to }
}

const span = (t: Trend) => `${formatPercent(t.from)} → ${formatPercent(t.to)}`

/**
 * 한 줄 판정 — 기억이 답한 비율의 추세와 실제 판정의 불량 통과율의 추세. 불량 통과율이 오르면 «쓸수록 나아진다» 가 깨진 것이라
 * 경고로 말한다. 판단할 구간(구간당 판정 10건 이상)이 둘 미만이면 추세를 말하지 않는다.
 */
export function trendLine(points: readonly FlywheelPoint[]): string {
  const usable = points.filter((p) => p.judged >= MIN_JUDGED)
  if (usable.length < 2) {
    return `아직 판단할 구간이 모자랍니다 — 구간당 판정 ${MIN_JUDGED}건 이상인 구간이 둘 이상 있어야 합니다.`
  }
  const parts: string[] = []
  const answered = trendOf(usable.map((p) => p.memory_answered_rate))
  if (answered) {
    const verb = answered.kind === 'up' ? '오릅니다' : answered.kind === 'down' ? '내려갑니다' : '평평합니다'
    parts.push(`기억이 답한 비율은 ${verb} (${span(answered)})`)
  }
  // 전용 ML — ML 이 라벨을 낸 판정이 구간당 10건 이상인 구간이 둘 이상일 때만(소표본 비율을 추세로 읽지 않는다).
  const mlSeries = points.flatMap((p) => (p.ml_checked >= MIN_JUDGED && p.ml_agreement !== undefined ? [p.ml_agreement] : []))
  const ml = trendOf(mlSeries)
  if (ml) {
    const verb = ml.kind === 'up' ? '오릅니다' : ml.kind === 'down' ? '내려갑니다' : '평평합니다'
    parts.push(`ML 라벨 일치율은 ${verb} (${span(ml)})`)
  }
  const pass = trendOf(usable.flatMap((p) => (p.operative_defect_pass === undefined ? [] : [p.operative_defect_pass])))
  if (pass) {
    parts.push(
      pass.kind === 'up'
        ? `불량 통과율이 오릅니다 (${span(pass)}) — 확인이 필요합니다`
        : `불량 통과율은 오르지 않았습니다 (${span(pass)})`,
    )
  }
  return parts.join(' · ')
}

export interface FlywheelRow {
  through: number
  judged: number
  operativeAgreement: string
  defectPass: string
  memoryAnswered: string
  memoryAgreement: string
  memoryMissed: number
  mlAgreement: string
  mlMissed: number
}

/** ML 이 라벨을 낸 판정이 하나라도 있나 — 없으면(ML 을 켜지 않은 설치) 표 · 그래프에 ML 열 · 선을 보이지 않는다. */
export function hasMlData(points: readonly FlywheelPoint[]): boolean {
  return points.some((p) => p.ml_checked > 0)
}

/** 표 행 — 비율은 퍼센트로, 분모가 0 이라 서버가 생략한 값은 «—». */
export function flywheelRows(points: readonly FlywheelPoint[]): FlywheelRow[] {
  return points.map((p) => ({
    through: p.labeled_through,
    judged: p.judged,
    operativeAgreement: formatPercent(p.operative_agreement ?? null),
    defectPass: formatPercent(p.operative_defect_pass ?? null),
    memoryAnswered: `${formatPercent(p.memory_answered_rate)} (${p.memory_answered})`,
    memoryAgreement: formatPercent(p.memory_agreement ?? null),
    memoryMissed: p.memory_missed,
    mlAgreement: p.ml_agreement === undefined ? '—' : `${formatPercent(p.ml_agreement)} (${p.ml_checked})`,
    mlMissed: p.ml_missed,
  }))
}

/**
 * 선 그래프 좌표 — 값(0~1)을 `width × height` 안의 점들로. 없는 값(null · undefined)은 선을 끊어 이어 그리지 않는다.
 * 반환은 이어진 조각마다 `"x,y x,y …"` 한 줄(SVG `polyline` 의 `points`). 점이 하나뿐이면 가운데에 둔다.
 */
export function chartPoints(values: readonly (number | null | undefined)[], width: number, height: number): string[] {
  const n = values.length
  const fmt = (x: number) => String(Number(x.toFixed(1)))
  const segments: string[] = []
  let current: string[] = []
  values.forEach((v, i) => {
    if (v === null || v === undefined) {
      if (current.length) segments.push(current.join(' '))
      current = []
      return
    }
    const x = n === 1 ? width / 2 : (i * width) / (n - 1)
    current.push(`${fmt(x)},${fmt((1 - v) * height)}`)
  })
  if (current.length) segments.push(current.join(' '))
  return segments
}
