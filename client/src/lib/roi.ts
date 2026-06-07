import type { ScenarioRoi } from './types'

/** 검사 관심영역(ROI) — 0~1 상대 좌표(해상도 독립). */
export interface Roi {
  x: number
  y: number
  width: number
  height: number
}

/** 기본 ROI(중앙) — 시나리오 ROI 가 비었을 때의 표시 기본. */
export const DEFAULT_ROI: Roi = { x: 0.2, y: 0.2, width: 0.6, height: 0.6 }

/**
 * 서버 wire(`ScenarioRoi`, `w`/`h`) ↔ 클라 `Roi`(`width`/`height`) 변환.
 * 둘 다 0~1 상대좌표이므로 **필드명 매핑만**(해상도 변환 없음 — cycle-21 ROI=relative 통일).
 */
export function fromScenarioRoi(s: ScenarioRoi): Roi {
  return { x: s.x, y: s.y, width: s.w, height: s.h }
}

export function toScenarioRoi(r: Roi): ScenarioRoi {
  return { x: r.x, y: r.y, w: r.width, h: r.height }
}

/** 값을 [0,1] 로 자른다(에디터 드래그가 박스를 벗어나지 않도록). */
export function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v
}
