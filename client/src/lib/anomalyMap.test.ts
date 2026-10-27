import { describe, expect, it } from 'vitest'

import { heatCells, hottestCells } from './anomalyMap'
import type { AnomalyMap } from './types'

function map(partial: Partial<AnomalyMap> & Pick<AnomalyMap, 'rows' | 'columns' | 'distances'>): AnomalyMap {
  return { encoder: 'e', patch_score: Math.max(...partial.distances), global_score: 0.01, bank_size: 200, ...partial }
}

describe('heatCells', () => {
  it('crop 이 없으면 격자가 이미지 전체를 덮는다 — 칸은 퍼센트로 등분', () => {
    const cells = heatCells(map({ rows: 2, columns: 2, distances: [0.1, 0.1, 0.1, 0.5] }), 400, 200)
    expect(cells).toHaveLength(4)
    expect(cells[0]).toMatchObject({ left: 0, top: 0, width: 50, height: 50 })
    expect(cells[3]).toMatchObject({ left: 50, top: 50, width: 50, height: 50, distance: 0.5 })
  })

  it('crop 이 있으면 그 영역(원본 픽셀)만큼만 덮고 나머지는 비운다', () => {
    // 이미지 400×200, 덮은 영역은 가운데 정사각 (100,0)~(300,200).
    const cells = heatCells(
      map({ rows: 1, columns: 2, distances: [0.1, 0.3], crop: { x0: 100, y0: 0, x1: 300, y1: 200 } }),
      400,
      200,
    )
    expect(cells[0]).toMatchObject({ left: 25, top: 0, width: 25, height: 100 })
    expect(cells[1]).toMatchObject({ left: 50, top: 0, width: 25, height: 100 })
  })

  it('세기는 이 이미지 안의 상대값 — 중앙값 이하는 0, 최댓값은 1', () => {
    const cells = heatCells(map({ rows: 1, columns: 5, distances: [0.1, 0.1, 0.1, 0.2, 0.5] }), 100, 100)
    expect(cells.map((c) => c.intensity)).toEqual([0, 0, 0, 0.25, 1])
  })

  it('모든 칸이 같으면 아무 칸도 달아오르지 않는다', () => {
    const cells = heatCells(map({ rows: 2, columns: 2, distances: [0.2, 0.2, 0.2, 0.2] }), 100, 100)
    expect(cells.every((c) => c.intensity === 0)).toBe(true)
  })

  it('격자와 거리 수가 맞지 않거나 이미지 크기를 모르면 그리지 않는다', () => {
    expect(heatCells(map({ rows: 2, columns: 2, distances: [0.1, 0.2, 0.3] }), 100, 100)).toEqual([])
    expect(heatCells(map({ rows: 1, columns: 1, distances: [0.1] }), 0, 100)).toEqual([])
    expect(heatCells(map({ rows: 1, columns: 1, distances: [0.1], crop: { x0: 5, y0: 0, x1: 5, y1: 10 } }), 100, 100)).toEqual([])
  })
})

describe('hottestCells', () => {
  it('가장 먼 칸부터 (행, 열) 로', () => {
    const m = map({ rows: 2, columns: 3, distances: [0.1, 0.4, 0.2, 0.4, 0.05, 0.3] })
    expect(hottestCells(m, 3)).toEqual([
      { row: 0, column: 1, distance: 0.4 },
      { row: 1, column: 0, distance: 0.4 },
      { row: 1, column: 2, distance: 0.3 },
    ])
  })
})
