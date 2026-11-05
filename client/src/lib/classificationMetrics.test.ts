import { describe, expect, it } from 'vitest'

import { agreementNotice, agreementSub, AGREEMENT_RELIABLE_LABELS, confusionTable, predictedRows } from './classificationMetrics'

// 서버 모양: 정답(사람 라벨) → 예측 → 건수.
const confusion = {
  A100: { A100: 8, B200: 2 },
  B200: { B200: 5 },
  reject: { A100: 1, reject: 3 },
}

describe('confusionTable', () => {
  it('선언한 순서로 행 · 열을 맞추고 줄마다 재현율(대각 / 줄 합)을 낸다', () => {
    const t = confusionTable({ confusion, predicted: {} }, ['reject', 'A100', 'B200'])
    expect(t.classes).toEqual(['reject', 'A100', 'B200'])
    expect(t.rows).toEqual([
      { truth: 'reject', cells: [3, 1, 0], total: 4, recall: 0.75 },
      { truth: 'A100', cells: [0, 8, 2], total: 10, recall: 0.8 },
      { truth: 'B200', cells: [0, 0, 5], total: 5, recall: 1 },
    ])
  })

  it('예측으로만 나온 클래스도 정답 쪽에 줄이 있어야 대각선이 맞다 — 줄 합 0 이면 재현율은 null(0% 로 위장하지 않는다)', () => {
    const t = confusionTable({ confusion: { A: { A: 2, B: 1 } }, predicted: {} }, ['A', 'B'])
    expect(t.classes).toEqual(['A', 'B'])
    expect(t.rows[1]).toEqual({ truth: 'B', cells: [0, 0], total: 0, recall: null })
  })

  it('선언하지 않았거나 한 번도 안 나온 클래스: 안 나온 선언 클래스는 빼고, 선언 밖은 이름 순으로 뒤에 잇는다', () => {
    const t = confusionTable({ confusion: { A: { A: 1, Z: 1 }, Y: { Y: 1 } }, predicted: {} }, ['A', 'never', 'B'])
    expect(t.classes).toEqual(['A', 'Y', 'Z'])
  })

  it('대소문자만 다른 철자는 한 클래스로 합친다', () => {
    const t = confusionTable({ confusion: { A: { A: 1 }, a: { a: 2, B: 1 } }, predicted: {} }, ['A', 'B'])
    expect(t.rows[0]).toEqual({ truth: 'A', cells: [3, 1], total: 4, recall: 0.75 })
  })

  it('사람 라벨이 붙은 판정이 없으면 빈 표', () => {
    expect(confusionTable({ confusion: {}, predicted: { A: 3 } }, ['A'])).toEqual({ classes: [], rows: [] })
  })
})

describe('predictedRows', () => {
  it('많은 순, 같으면 이름 순', () => {
    expect(predictedRows({ predicted: { B: 2, A: 2, C: 5 } })).toEqual([
      { id: 'C', count: 5 },
      { id: 'A', count: 2 },
      { id: 'B', count: 2 },
    ])
  })
})

describe('agreementNotice', () => {
  it('판정이 없으면 말하지 않고, 라벨이 없으면 일치율을 낼 수 없다고 말한다', () => {
    expect(agreementNotice({ checked: 0, labeled: 0 })).toBeNull()
    expect(agreementNotice({ checked: 12, labeled: 0 })).toContain('사람 라벨이 아직 없어')
  })

  it('라벨이 모자라면 참고만 하라고, 충분하면 말하지 않는다', () => {
    expect(agreementNotice({ checked: 40, labeled: 7 })).toContain('7건뿐')
    expect(agreementNotice({ checked: 40, labeled: AGREEMENT_RELIABLE_LABELS })).toBeNull()
  })
})

describe('agreementSub', () => {
  it('«일치/라벨 일치» 로', () => {
    expect(agreementSub({ agreed: 12, labeled: 15 })).toBe('12/15 일치')
  })
})
