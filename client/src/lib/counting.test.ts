import { describe, expect, it } from 'vitest'

import { countDraftOf, countLine, EMPTY_COUNT_DRAFT, isCounting, MAX_COUNT, parseCountDraft } from './counting'

describe('isCounting', () => {
  it('유형이 counting 일 때만 — 사진 확인(문제 큐)을 숨기는 근거', () => {
    expect(isCounting({ type: 'counting' })).toBe(true)
    expect(isCounting({ type: 'inspection' })).toBe(false)
    expect(isCounting({ type: 'verification' })).toBe(false)
    expect(isCounting({})).toBe(false)
  })
})

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
      parseCountDraft({ ...EMPTY_COUNT_DRAFT, expected: ' 6 ', tolerance: '1', minConfidence: '0.7', kinds: 'helmet, head\n  person ' }),
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
      ...EMPTY_COUNT_DRAFT,
      expected: '4',
      kinds: 'helmet, head',
    })
    expect(countDraftOf({})).toEqual(EMPTY_COUNT_DRAFT)
  })

  it('입력란 → 서버 필드 → 입력란이 값을 바꾸지 않는다', () => {
    const draft = { ...EMPTY_COUNT_DRAFT, expected: '6', tolerance: '1', minConfidence: '0.7', kinds: 'helmet, head' }
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

describe('parseCountDraft — 종류별 기대(구성 확인)', () => {
  const byClass = { ...EMPTY_COUNT_DRAFT, mode: 'by_class' as const }

  it('«종류=개수» 줄을 읽고 허용 오차 없이 과잉 처리와 함께 보낸다 — 총 개수 필드는 보내지 않는다', () => {
    const parsed = parseCountDraft({ ...byClass, byClass: ' scalpel=2\nforceps = 1, gauze=0 ', extraPolicy: 'warn' })

    expect(parsed).toEqual({
      fields: { expected_by_class: { scalpel: 2, forceps: 1, gauze: 0 }, extra_policy: 'warn', count_classes: [] },
    })
    expect(parsed).not.toHaveProperty('fields.expected_count')
    expect(parsed).not.toHaveProperty('fields.count_tolerance')
  })

  it('종류 이름에 = 가 있어도 마지막 = 로 나눈다', () => {
    expect(parseCountDraft({ ...byClass, byClass: 'a=b=3' })).toHaveProperty('fields.expected_by_class', { 'a=b': 3 })
  })

  it('총 개수 칸의 값은 종류별 모드에서 보내지 않는다 — 서버가 둘을 함께 받으면 400', () => {
    const parsed = parseCountDraft({ ...byClass, byClass: 'a=1', expected: '9', tolerance: '2' })

    expect(parsed).not.toHaveProperty('fields.expected_count')
    expect(parsed).not.toHaveProperty('fields.count_tolerance')
  })

  it.each([
    ['', '«종류=개수»'],
    ['scalpel', '형식이 아니다'],
    ['=2', '종류 이름'],
    ['scalpel=', '정수'],
    ['scalpel=-1', '정수'],
    ['scalpel=1.5', '정수'],
    [`scalpel=${MAX_COUNT + 1}`, '정수'],
    ['scalpel=1\nscalpel=2', '겹친다'],
    [`${'x'.repeat(65)}=1`, '1~64자'],
  ])('서버가 거부할 입력은 사유와 함께 막는다: %j', (text, reason) => {
    const result = parseCountDraft({ ...byClass, byClass: text })

    expect(result).toHaveProperty('error')
    expect((result as { error: string }).error).toContain(reason)
  })

  it('세는 종류를 채웠다면 기대 종류를 모두 포함해야 한다 — 서버와 같은 모순 거절', () => {
    const result = parseCountDraft({ ...byClass, byClass: 'a=1\nb=1', kinds: 'a' })

    expect((result as { error: string }).error).toContain('b')
    expect(parseCountDraft({ ...byClass, byClass: 'a=1\nb=1', kinds: 'a, b, c' })).toHaveProperty('fields')
  })

  it('저장된 종류별 기대는 입력란으로 되돌아오고 다시 읽어도 값이 같다', () => {
    const draft = countDraftOf({ expected_by_class: { scalpel: 2, forceps: 1 }, extra_policy: 'ignore', count_classes: ['scalpel', 'forceps'] })

    expect(draft).toMatchObject({ mode: 'by_class', extraPolicy: 'ignore', byClass: 'scalpel=2\nforceps=1' })
    const parsed = parseCountDraft(draft)
    if (!('fields' in parsed)) throw new Error('parse failed')
    expect(parsed.fields).toMatchObject({ expected_by_class: { scalpel: 2, forceps: 1 }, extra_policy: 'ignore' })
  })

  it('저장된 정책이 없거나 모르는 값이면 기본(ng)으로 읽는다', () => {
    expect(countDraftOf({ expected_by_class: { a: 1 } }).extraPolicy).toBe('ng')
    expect(countDraftOf({ expected_by_class: { a: 1 }, extra_policy: 'someday' }).extraPolicy).toBe('ng')
  })

  it('목록 한 줄은 구성과 과잉 처리를 보인다', () => {
    expect(countLine({ type: 'counting', expected_by_class: { scalpel: 2, forceps: 1 }, extra_policy: 'warn', count_classes: [] })).toBe(
      '구성 scalpel 2 · forceps 1 · 과잉 경고',
    )
    expect(countLine({ type: 'counting', expected_by_class: { a: 1 } })).toBe('구성 a 1 · 과잉 불량')
  })
})
