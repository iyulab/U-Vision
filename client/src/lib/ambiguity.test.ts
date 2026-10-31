import { describe, expect, it } from 'vitest'

import { groupLine, hasClosedQuestions } from './ambiguity'
import type { Ambiguity } from './types'

describe('groupLine', () => {
  it('닫힌 문제가 모자라면 비율 대신 이유를 말한다', () => {
    expect(groupLine({ answered: 4, abstained: 2, open: 0 }, 10)).toBe('답 4 · 모르겠음 2 · 비율은 닫힌 10건부터(지금 6건)')
  })

  it('비율이 있으면 퍼센트로, 미답이 있으면 함께 적는다', () => {
    expect(groupLine({ answered: 9, abstained: 3, open: 2, abstain_rate: 0.25 }, 10)).toBe('답 9 · 모르겠음 3 · 미답 2 · 모르겠음 비율 25%')
  })
})

describe('hasClosedQuestions', () => {
  const empty = {
    scenario_id: 's',
    min_answers_for_rate: 10,
    representative: { answered: 0, abstained: 0, open: 0 },
    targeted: { answered: 0, abstained: 0, open: 0 },
  } as Ambiguity

  it('닫힌 문제가 없으면(기본) 카드를 그리지 않는다 — 열린 문제만 있어도 마찬가지', () => {
    expect(hasClosedQuestions(undefined)).toBe(false)
    expect(hasClosedQuestions(empty)).toBe(false)
    expect(hasClosedQuestions({ ...empty, targeted: { answered: 0, abstained: 0, open: 3 } })).toBe(false)
    expect(hasClosedQuestions({ ...empty, targeted: { answered: 1, abstained: 0, open: 0 } })).toBe(true)
  })
})
