import type { HealthStatus } from './types'

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const

/** 바이트 수를 읽기 쉬운 단위로(1024 단위 — GB 이상은 소수 한 자리, 그 아래는 정수). */
export function formatBytes(bytes: number): string {
  let value = Math.max(0, bytes)
  let i = 0
  while (value >= 1024 && i < UNITS.length - 1) {
    value /= 1024
    i++
  }
  return `${i >= 3 ? value.toFixed(1) : Math.round(value)} ${UNITS[i]}`
}

export interface VolumeSummary {
  /** «여유 12.3 GB / 전체 250.0 GB» */
  text: string
  /** 서버가 정한 문턱(`low`) 미만이면 true — 화면은 경고로 그린다. */
  low: boolean
}

/**
 * `GET /health` 의 데이터 볼륨 여유를 화면 문구로. 서버가 볼륨을 읽지 못하면(UNC 경로 · 준비되지 않은 드라이브) 필드가 없어
 * null — 화면은 아무것도 그리지 않는다(모른다는 것을 «여유 있음» 으로 보이지 않게).
 */
export function summarizeVolume(health: HealthStatus | null | undefined): VolumeSummary | null {
  const v = health?.data_volume
  if (!v) return null
  return { text: `여유 ${formatBytes(v.free_bytes)} / 전체 ${formatBytes(v.total_bytes)}`, low: v.low }
}
