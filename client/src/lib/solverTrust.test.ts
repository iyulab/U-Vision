import { describe, expect, it } from 'vitest'

import { deviceLine, hasPlants } from './solverTrust'
import type { SolverTrust } from './types'

describe('deviceLine', () => {
  it('답한 건수가 모자라면 정답률 대신 이유를 말한다', () => {
    expect(deviceLine({ correct: 2, wrong: 1, unsure: 0, open: 0 }, 10)).toBe(
      '정답 2 · 오답 1 · 모르겠음 0 · 정답률은 답한 10건부터(지금 3건)',
    )
  })

  it('정답률이 있으면 퍼센트로, 미답이 있으면 함께 적는다', () => {
    expect(deviceLine({ correct: 9, wrong: 1, unsure: 4, open: 2, accuracy: 0.9 }, 10)).toBe(
      '정답 9 · 오답 1 · 모르겠음 4 · 미답 2 · 정답률 90%',
    )
  })
})

describe('hasPlants', () => {
  const base = { scenario_id: 's', planted: 0, correct: 0, wrong: 0, unsure: 0, open: 0, min_answers_for_rate: 10, devices: [] } as SolverTrust

  it('심은 문제가 없으면(기본) 카드를 그리지 않는다', () => {
    expect(hasPlants(undefined)).toBe(false)
    expect(hasPlants(base)).toBe(false)
    expect(hasPlants({ ...base, planted: 1 })).toBe(true)
  })
})
