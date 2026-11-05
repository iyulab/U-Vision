import { describe, expect, it } from 'vitest'

import {
  blankClass,
  classDraftsError,
  classDraftsOf,
  classesOfDrafts,
  classSummary,
  emptyClassDrafts,
  isClassification,
  MAX_CLASS_DESCRIPTION,
  sameClassDrafts,
  type ClassDraft,
} from './classification'
import { defectsOf } from './defectClasses'

const row = (over: Partial<ClassDraft>): ClassDraft => ({ ...blankClass(), ...over })

describe('isClassification', () => {
  it('유형이 classification 일 때만', () => {
    expect(isClassification({ type: 'classification' })).toBe(true)
    expect(isClassification({ type: 'inspection' })).toBe(false)
    expect(isClassification({})).toBe(false)
  })
})

describe('classDraftsOf', () => {
  it('저장된 선별 클래스를 줄로 — 표시 이름 · 설명 · 플래그를 읽고 저장된 줄로 표시한다', () => {
    const drafts = classDraftsOf({
      type: 'classification',
      classes: [
        { id: 'A100', label: '품번 A', description: '금색 커넥터', settleable: true },
        { id: 'reject', must_catch: true },
      ],
    })
    expect(drafts).toEqual([
      { id: 'A100', label: '품번 A', description: '금색 커넥터', settleable: true, mustCatch: false, saved: true },
      { id: 'reject', label: '', description: '', settleable: false, mustCatch: true, saved: true },
    ])
  })

  it('선별이 아니거나 클래스가 없으면 빈 줄 둘(유형을 바꿀 때의 시작점) — 검사의 OK · NG 를 옮겨 오지 않는다', () => {
    expect(classDraftsOf({ type: 'inspection', classes: [{ id: 'OK' }, { id: 'NG' }] })).toEqual(emptyClassDrafts())
    expect(classDraftsOf({ type: 'classification' })).toEqual(emptyClassDrafts())
    expect(emptyClassDrafts()).toHaveLength(2)
  })
})

describe('classDraftsError', () => {
  const ok = [row({ id: 'A' }), row({ id: 'B' })]

  it('값 중립 클래스 둘 이상이면 통과 — OK · 불량 유형을 요구하지 않는다', () => {
    expect(classDraftsError(ok)).toBeNull()
    expect(classDraftsError([row({ id: 'A', settleable: true }), row({ id: 'B', mustCatch: true }), row({ id: 'C' })])).toBeNull()
  })

  it('둘 미만 · 빈 id · 형식 위반 · 겹침(대소문자 무시)을 막는다', () => {
    expect(classDraftsError([row({ id: 'A' })])).toMatch(/둘 이상/)
    expect(classDraftsError([row({ id: 'A' }), row({ id: '' })])).toMatch(/빈 칸/)
    expect(classDraftsError([row({ id: 'A' }), row({ id: '품번' })])).toMatch(/id/)
    expect(classDraftsError([row({ id: 'A' }), row({ id: 'a' })])).toMatch(/겹친다/)
    expect(classDraftsError([row({ id: 'A' }), row({ id: 'x'.repeat(65) })])).toMatch(/id/)
  })

  it('표시 이름 규칙(1~32자 · , = 금지 · 겹침)과 설명 규칙(400자 · 줄바꿈 금지)', () => {
    expect(classDraftsError([row({ id: 'A', label: 'a=b' }), row({ id: 'B' })])).toMatch(/표시 이름/)
    expect(classDraftsError([row({ id: 'A', label: '가'.repeat(33) }), row({ id: 'B' })])).toMatch(/표시 이름/)
    expect(classDraftsError([row({ id: 'A', label: '같음' }), row({ id: 'B', label: '같음' })])).toMatch(/표시 이름이 겹친다/)
    expect(classDraftsError([row({ id: 'A', description: '한 줄\n두 줄' }), row({ id: 'B' })])).toMatch(/설명/)
    expect(classDraftsError([row({ id: 'A', description: 'd'.repeat(MAX_CLASS_DESCRIPTION + 1) }), row({ id: 'B' })])).toMatch(/설명/)
    expect(classDraftsError([row({ id: 'A', description: 'd'.repeat(MAX_CLASS_DESCRIPTION) }), row({ id: 'B' })])).toBeNull()
  })

  it('확정 허용과 꼭 잡을 값은 함께 켤 수 없다(서버와 같다)', () => {
    expect(classDraftsError([row({ id: 'A', settleable: true, mustCatch: true }), row({ id: 'B' })])).toMatch(/함께/)
  })

  it('비운 표시 이름 · 설명은 «없음» 이라 규칙에 걸리지 않는다 — 공백만 있어도 같다', () => {
    expect(classDraftsError([row({ id: 'A', label: '   ', description: '  ' }), row({ id: 'B' })])).toBeNull()
  })
})

describe('classesOfDrafts', () => {
  it('비운 필드 · 꺼진 플래그는 보내지 않고 앞뒤 공백을 다듬는다', () => {
    expect(
      classesOfDrafts([
        row({ id: ' A ', label: ' 품번 A ', description: ' 금색 ', settleable: true }),
        row({ id: 'B', label: ' ', mustCatch: true }),
        row({ id: 'C' }),
      ]),
    ).toEqual([{ id: 'A', label: '품번 A', description: '금색', settleable: true }, { id: 'B', must_catch: true }, { id: 'C' }])
  })

  it('저장된 시나리오를 줄로 읽었다가 다시 만들면 그대로다(왕복)', () => {
    const classes = [
      { id: 'A', label: '품번 A', description: '금색', settleable: true },
      { id: 'B', must_catch: true },
      { id: 'C' },
    ]
    expect(classesOfDrafts(classDraftsOf({ type: 'classification', classes }))).toEqual(classes)
  })
})

describe('sameClassDrafts', () => {
  it('보내는 모양이 같으면 같다 — 공백만 다른 입력은 변경이 아니다', () => {
    expect(sameClassDrafts([row({ id: 'A' }), row({ id: 'B' })], [row({ id: ' A ' }), row({ id: 'B' })])).toBe(true)
    expect(sameClassDrafts([row({ id: 'A' }), row({ id: 'B' })], [row({ id: 'A', mustCatch: true }), row({ id: 'B' })])).toBe(false)
  })
})

describe('classSummary · defectsOf', () => {
  it('목록 배지는 표시 이름이 있으면 그 이름을 쓴다', () => {
    expect(classSummary({ classes: [{ id: 'A', label: '품번 A' }, { id: 'B' }] })).toBe('품번 A · B')
    expect(classSummary({})).toBe('')
  })

  it('선별의 클래스는 «불량 유형» 이 아니다 — 검사의 불량 유형 입력란이 그것을 끌어오지 않는다', () => {
    expect(defectsOf({ type: 'classification', classes: [{ id: 'A' }, { id: 'B' }] })).toEqual([])
  })
})
