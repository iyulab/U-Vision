import { describe, expect, it } from 'vitest'

import { countDraftOf, countLine, EMPTY_COUNT_DRAFT, MAX_COUNT, parseCountDraft } from './counting'

describe('parseCountDraft', () => {
  it('기대 개수만 있어도 된다 — 허용 오차는 0, 종류는 비어 있고, 최소 신뢰도는 보내지 않는다', () => {
    expect(parseCountDraft({ ...EMPTY_COUNT_DRAFT, expected: '6' })).toEqual({
      fields: { expected_count: 6, count_tolerance: 0, count_classes: [] },
    })
  })

  it('기대 0 도 받는다 — 아무것도 없어야 하는 사진(이물 계수)', () => {
    expect(parseCountDraft({ ...EMPTY_COUNT_DRAFT, expected: '0' })).toHaveProperty('fields.expected_count', 0)
  })

  it('모든 칸을 읽는다 — 공백을 다듬고 종류는 쉼표 · 줄바꿈으로 나눈다', () => {
    expect(
      parseCountDraft({ expected: ' 6 ', tolerance: '1', minConfidence: '0.7', kinds: 'helmet, head\n  person ' }),
    ).toEqual({
      fields: { expected_count: 6, count_tolerance: 1, min_confidence: 0.7, count_classes: ['helmet', 'head', 'person'] },
    })
  })

  it.each([
    [{ expected: '' }, '기대 개수를 입력'],
    [{ expected: '-1' }, '정수'],
    [{ expected: '2.5' }, '정수'],
    [{ expected: 'six' }, '정수'],
    [{ expected: String(MAX_COUNT + 1) }, '정수'],
    [{ expected: '3', tolerance: '-1' }, '허용 오차'],
    [{ expected: '3', tolerance: '1.5' }, '허용 오차'],
    [{ expected: '3', minConfidence: '0' }, '신뢰도'],
    [{ expected: '3', minConfidence: '1.5' }, '신뢰도'],
    [{ expected: '3', minConfidence: 'high' }, '신뢰도'],
    [{ expected: '3', kinds: 'a, a' }, '겹친다'],
    [{ expected: '3', kinds: 'x'.repeat(65) }, '1~64자'],
  ])('서버가 거부할 입력은 사유와 함께 막는다: %j', (partial, reason) => {
    const result = parseCountDraft({ ...EMPTY_COUNT_DRAFT, ...partial })
    expect(result).toHaveProperty('error')
    expect((result as { error: string }).error).toContain(reason)
  })
})

describe('countDraftOf', () => {
  it('저장된 값을 입력란으로 되돌린다 — 없는 칸은 빈 문자열', () => {
    expect(countDraftOf({ expected_count: 4, count_classes: ['helmet', 'head'] })).toEqual({
      expected: '4',
      tolerance: '',
      minConfidence: '',
      kinds: 'helmet, head',
    })
    expect(countDraftOf({})).toEqual(EMPTY_COUNT_DRAFT)
  })

  it('입력란 → 서버 필드 → 입력란이 값을 바꾸지 않는다', () => {
    const draft = { expected: '6', tolerance: '1', minConfidence: '0.7', kinds: 'helmet, head' }
    const parsed = parseCountDraft(draft)
    if (!('fields' in parsed)) throw new Error('parse failed')
    expect(countDraftOf(parsed.fields)).toEqual(draft)
  })
})

describe('countLine', () => {
  it('계수만 한 줄로 보인다', () => {
    expect(countLine({ type: 'counting', expected_count: 6, count_tolerance: 1, min_confidence: 0.7, count_classes: ['helmet'] })).toBe(
      '기대 6 ± 1 · 신뢰도 0.7 이상 · helmet',
    )
    expect(countLine({ type: 'counting', expected_count: 3 })).toBe('기대 3 ± 0')
    expect(countLine({ type: 'verification' })).toBeNull()
    expect(countLine({})).toBeNull()
  })
})
