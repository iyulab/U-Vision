/**
 * 확정 사례 기억 표시 순수 로직 — 서버 `GET /memory` 의 상태 한 줄과 문턱 재생 행.
 * 기억은 아직 판정을 바꾸지 않는다(관찰 전용) — 재생 표는 «언제 맡겨도 되나»를 읽는 자리다.
 */

import type { MemoryReplayRow, MemoryReport } from './types'

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
