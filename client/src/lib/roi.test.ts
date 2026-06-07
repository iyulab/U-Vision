import { describe, expect, it } from 'vitest'

import { clamp01, fromScenarioRoi, toScenarioRoi, type Roi } from './roi'
import type { ScenarioRoi } from './types'

describe('ROI 변환 (서버 wire ↔ 클라)', () => {
  it('toScenarioRoi 는 width/height → w/h 로 매핑한다', () => {
    const roi: Roi = { x: 0.1, y: 0.2, width: 0.5, height: 0.6 }
    expect(toScenarioRoi(roi)).toEqual({ x: 0.1, y: 0.2, w: 0.5, h: 0.6 })
  })

  it('fromScenarioRoi 는 w/h → width/height 로 매핑한다', () => {
    const wire: ScenarioRoi = { x: 0.1, y: 0.2, w: 0.5, h: 0.6 }
    expect(fromScenarioRoi(wire)).toEqual({ x: 0.1, y: 0.2, width: 0.5, height: 0.6 })
  })

  it('왕복(round-trip)이 값을 보존한다', () => {
    const roi: Roi = { x: 0.05, y: 0.15, width: 0.7, height: 0.25 }
    expect(fromScenarioRoi(toScenarioRoi(roi))).toEqual(roi)
  })
})

describe('clamp01', () => {
  it('범위 밖 값을 [0,1] 로 자른다', () => {
    expect(clamp01(-0.3)).toBe(0)
    expect(clamp01(1.7)).toBe(1)
    expect(clamp01(0.42)).toBe(0.42)
  })
})
