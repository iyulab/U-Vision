import { describe, expect, it } from 'vitest'

import { boxStyles, countSummary, NEAR_THRESHOLD_BAND } from './countBoxes'

const box = (x: number, confidence: number, cls = 'pill') => ({ class: cls, x, y: 0.2, w: 0.1, h: 0.3, confidence })

describe('boxStyles', () => {
  it('비율 좌표를 퍼센트 사각형으로 옮긴다', () => {
    const [s] = boxStyles({ min_confidence: 0.5, boxes: [box(0.25, 0.9)] })
    expect(s).toMatchObject({ left: 25, top: 20, label: 'pill', confidence: 0.9, near: false })
    expect(s.width).toBeCloseTo(10)
    expect(s.height).toBeCloseTo(30)
  })

  it('문턱 ± 띠 안의 상자만 near 다 — 경계 포함', () => {
    const styles = boxStyles({
      min_confidence: 0.5,
      boxes: [box(0, 0.5 + NEAR_THRESHOLD_BAND), box(0, 0.5 - NEAR_THRESHOLD_BAND), box(0, 0.75), box(0, 0.99)],
    })
    expect(styles.map((s) => s.near)).toEqual([true, true, false, false])
  })

  it('이미지 밖으로 나간 값은 0~100 으로 자른다', () => {
    const [s] = boxStyles({ min_confidence: 0.5, boxes: [{ class: 'a', x: -0.1, y: 0.9, w: 1.4, h: 0.4, confidence: 0.9 }] })
    expect(s).toMatchObject({ left: 0, width: 100, height: 40 })
  })

  it('상자가 없으면 빈 목록', () => {
    expect(boxStyles({ min_confidence: 0.5, boxes: [] })).toEqual([])
  })
})

describe('countSummary', () => {
  it('센 수와 기대를 한 줄로, 종류가 여럿일 때만 나눠 보인다', () => {
    expect(countSummary({ total: 5, expected: 6, tolerance: 1, by_class: { pill: 5 } })).toBe('센 개수 5 (기대 6 ± 1)')
    expect(countSummary({ total: 5, expected: 6, tolerance: 0, by_class: { helmet: 3, head: 2 } })).toBe(
      '센 개수 5 (기대 6 ± 0) — helmet 3 · head 2',
    )
  })
})
