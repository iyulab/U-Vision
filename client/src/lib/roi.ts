/** 검사 관심영역(ROI) — 0~1 상대 좌표(해상도 독립). */
export interface Roi {
  x: number
  y: number
  width: number
  height: number
}

/** 하드코딩 기본 ROI(중앙). 시각 편집기는 P2(관리자 셋업)에서. */
export const DEFAULT_ROI: Roi = { x: 0.2, y: 0.2, width: 0.6, height: 0.6 }
