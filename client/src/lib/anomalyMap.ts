import type { AnomalyMap } from './types'

/** 화면에 그릴 격자 칸 하나 — 위치 · 크기는 이미지 표시 영역에 대한 퍼센트, `intensity` 는 0~1. */
export interface HeatCell {
  left: number
  top: number
  width: number
  height: number
  intensity: number
  /** 이 칸의 실제 거리(코사인) — 툴팁 · 접근성 텍스트용. */
  distance: number
}

/**
 * 이상 지도 → 칸 목록. 격자는 `crop`(원본 픽셀)을 `rows × columns` 로 등분한 것이라, 이미지의 자연 크기를 알아야 퍼센트로 옮길 수
 * 있다. `crop` 이 없으면 격자가 이미지 전체를 덮는다고 본다. 거리 수가 격자와 맞지 않으면 그릴 수 없으니 빈 목록.
 *
 * 색의 세기는 **이 이미지 안에서의 상대값**이다: 칸 거리의 중앙값(그 이미지의 «보통» 변이)을 0, 최댓값을 1 로 늘린다. 정상 기억에서
 * 유난히 먼 자리만 달아오르고 이미지 전체가 붉어지지 않지만, 어느 이미지든 가장 먼 자리는 가장 진하게 칠해진다 — 절대 크기는
 * `patch_score` 로 따로 보인다(색만 보고 «불량»으로 읽지 않게).
 */
export function heatCells(map: AnomalyMap, imageWidth: number, imageHeight: number): HeatCell[] {
  const { rows, columns, distances } = map
  if (rows < 1 || columns < 1 || distances.length !== rows * columns) return []
  if (!(imageWidth > 0) || !(imageHeight > 0)) return []

  const crop = map.crop ?? { x0: 0, y0: 0, x1: imageWidth, y1: imageHeight }
  const cropW = crop.x1 - crop.x0
  const cropH = crop.y1 - crop.y0
  if (!(cropW > 0) || !(cropH > 0)) return []

  const sorted = [...distances].sort((a, b) => a - b)
  const median = sorted[Math.floor(sorted.length / 2)]
  const max = sorted[sorted.length - 1]
  const span = max - median

  return distances.map((distance, i) => {
    const r = Math.floor(i / columns)
    const c = i % columns
    return {
      left: ((crop.x0 + (cropW * c) / columns) / imageWidth) * 100,
      top: ((crop.y0 + (cropH * r) / rows) / imageHeight) * 100,
      width: (cropW / columns / imageWidth) * 100,
      height: (cropH / rows / imageHeight) * 100,
      intensity: span > 0 ? Math.min(1, Math.max(0, (distance - median) / span)) : 0,
      distance,
    }
  })
}

/** 가장 먼 칸 `n` 개의 (행, 열) — 설명 문구용. 거리가 같으면 앞선 칸이 먼저. */
export function hottestCells(map: AnomalyMap, n: number): { row: number; column: number; distance: number }[] {
  if (map.columns < 1) return []
  return map.distances
    .map((distance, i) => ({ row: Math.floor(i / map.columns), column: i % map.columns, distance, i }))
    .sort((a, b) => b.distance - a.distance || a.i - b.i)
    .slice(0, n)
    .map(({ row, column, distance }) => ({ row, column, distance }))
}
