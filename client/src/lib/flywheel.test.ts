import { describe, expect, it } from 'vitest'

import { chartPoints, flywheelRows, trendLine, trendOf } from './flywheel'
import type { FlywheelPoint } from './types'

const point = (over: Partial<FlywheelPoint>): FlywheelPoint => ({
  labeled_through: 25,
  judged: 25,
  labeled_defects: 0,
  operative_missed: 0,
  memory_answered: 0,
  memory_answered_rate: 0,
  memory_missed: 0,
  ...over,
})

describe('trendOf', () => {
  it('앞 절반과 뒤 절반의 평균을 견줘 오름 · 평평 · 내림', () => {
    expect(trendOf([0.1, 0.2, 0.4, 0.5])).toEqual({ kind: 'up', from: 0.15, to: 0.45 })
    expect(trendOf([0.4, 0.42, 0.41, 0.43])?.kind).toBe('flat')
    expect(trendOf([0.9, 0.8, 0.5, 0.4])?.kind).toBe('down')
  })

  it('값이 둘 미만이면 판단하지 않는다', () => {
    expect(trendOf([])).toBeNull()
    expect(trendOf([0.5])).toBeNull()
  })
})

describe('trendLine', () => {
  it('판단할 구간(구간당 판정 10건 이상)이 둘 미만이면 모자라다고 말한다', () => {
    const line = trendLine([point({ judged: 25 }), point({ judged: 4, labeled_through: 29 })])
    expect(line).toContain('모자랍니다')
    expect(line).toContain('10건')
  })

  it('기억이 답한 비율이 오르면 그 숫자와 함께 말한다', () => {
    const line = trendLine([
      point({ labeled_through: 25, memory_answered_rate: 0.1 }),
      point({ labeled_through: 50, memory_answered_rate: 0.3 }),
      point({ labeled_through: 75, memory_answered_rate: 0.5 }),
      point({ labeled_through: 100, memory_answered_rate: 0.6 }),
    ])
    expect(line).toContain('기억이 답한 비율은 오릅니다')
    expect(line).toContain('20% → 55%')
  })

  it('불량 통과율이 오르면 경고한다 — 쓸수록 나아진다는 주장이 깨진다', () => {
    const line = trendLine([
      point({ labeled_through: 25, operative_defect_pass: 0.0 }),
      point({ labeled_through: 50, operative_defect_pass: 0.0 }),
      point({ labeled_through: 75, operative_defect_pass: 0.2 }),
      point({ labeled_through: 100, operative_defect_pass: 0.3 }),
    ])
    expect(line).toContain('불량 통과율')
    expect(line).toContain('오릅니다')
  })
})

describe('flywheelRows', () => {
  it('비율은 퍼센트로, 분모가 0 이라 없는 값은 «—» 로', () => {
    const [row] = flywheelRows([point({ operative_agreement: 0.84, memory_answered: 3, memory_answered_rate: 0.12, memory_agreement: 1 })])
    expect(row).toEqual({
      through: 25,
      judged: 25,
      operativeAgreement: '84%',
      defectPass: '—',
      memoryAnswered: '12% (3)',
      memoryAgreement: '100%',
      memoryMissed: 0,
    })
  })
})

describe('chartPoints', () => {
  it('값을 좌표 문자열로 — 없는 값은 선을 끊는다', () => {
    const segments = chartPoints([0, 1, null, 0.5], 300, 100)
    expect(segments).toEqual(['0,100 100,0', '300,50'])
  })

  it('점이 하나뿐이어도 그린다(가운데)', () => {
    expect(chartPoints([0.5], 300, 100)).toEqual(['150,50'])
  })
})
