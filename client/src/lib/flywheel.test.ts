import { describe, expect, it } from 'vitest'

import { chartPoints, flywheelRows, hasMlData, trendLine, trendOf } from './flywheel'
import type { FlywheelPoint } from './types'

const point = (over: Partial<FlywheelPoint>): FlywheelPoint => ({
  labeled_through: 25,
  judged: 25,
  labeled_defects: 0,
  operative_missed: 0,
  memory_answered: 0,
  memory_answered_rate: 0,
  memory_missed: 0,
  ml_checked: 0,
  ml_missed: 0,
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
      mlAgreement: '—',
      mlMissed: 0,
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

describe('ML 단', () => {
  const ml = (through: number, agreement: number | undefined, checked = 25) =>
    point({ labeled_through: through, ml_checked: checked, ml_agreement: agreement })

  it('hasMlData — ML 이 라벨을 낸 판정이 하나라도 있을 때만', () => {
    expect(hasMlData([point({}), point({})])).toBe(false)
    expect(hasMlData([point({}), ml(50, 0.9, 3)])).toBe(true)
  })

  it('표 행은 ML 일치율과 «ML 이 OK 로 지나간 불량»', () => {
    const [row] = flywheelRows([point({ ml_checked: 20, ml_agreement: 0.95, ml_missed: 1 })])
    expect(row.mlAgreement).toBe('95% (20)')
    expect(row.mlMissed).toBe(1)
  })

  it('한 줄 판정에 ML 일치율 추세(ML 이 답한 구간 10건 이상이 둘 이상일 때만)', () => {
    const line = trendLine([ml(25, 0.6), ml(50, 0.7), ml(75, 0.9), ml(100, 0.95)])
    expect(line).toContain('ML 라벨 일치율은 오릅니다 (65% → 93%)')
    // ML 이 답한 구간이 모자라면 ML 문장은 빠진다 — 다른 문장은 그대로.
    const few = trendLine([ml(25, 0.6, 3), ml(50, 0.7, 4), point({ labeled_through: 75 }), point({ labeled_through: 100 })])
    expect(few).not.toContain('ML 라벨')
  })
})
