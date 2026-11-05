import { describe, expect, it } from 'vitest'
import { emptyClassNotice, groupByClass, missingReferenceNotice, splitReferences } from './references'
import type { Reference } from './types'

describe('splitReferences', () => {
  it('서버가 보내는 숫자 label(0 = ok, 1 = ng)로 나눈다', () => {
    // GET /scenarios/{id}/references 응답 모양 그대로.
    const wire = JSON.parse(
      '[{"ref_id":"r1","label":0,"ng_label":null},{"ref_id":"r2","label":1,"ng_label":"scratch"}]',
    ) as Reference[]

    const { ok, ng } = splitReferences(wire)

    expect(ok.map((r) => r.ref_id)).toEqual(['r1'])
    expect(ng.map((r) => r.ref_id)).toEqual(['r2'])
  })
})

describe('missingReferenceNotice', () => {
  const one = [{ ref_id: 'r1', label: 0, ng_label: null }] as unknown as Reference[]

  it('검사 시나리오에 기준 이미지가 하나도 없으면 안내한다', () => {
    expect(missingReferenceNotice([], true, 'inspection')).toContain('기준 이미지가 없습니다')
    expect(missingReferenceNotice([], true, undefined)).not.toBeNull() // type 이 없으면 검사(v1)
  })

  it('기준이 있거나 · 아직 못 읽었거나 · 오투입 확인이면 안내하지 않는다', () => {
    expect(missingReferenceNotice(one, true, 'inspection')).toBeNull()
    expect(missingReferenceNotice([], false, 'inspection')).toBeNull()
    expect(missingReferenceNotice([], true, 'verification')).toBeNull()
  })
})

describe('groupByClass', () => {
  // 선별 목록 응답: label 은 클래스의 OK/NG 보기(0/1), class 가 본체다.
  const wire = JSON.parse(
    '[{"ref_id":"r1","label":0,"ng_label":null,"class":"A100"},{"ref_id":"r2","label":0,"ng_label":null,"class":"A100"},' +
      '{"ref_id":"r3","label":1,"ng_label":null,"class":"reject"},{"ref_id":"r4","label":0,"ng_label":null,"class":"gone"}]',
  ) as Reference[]
  const classes = [{ id: 'A100', label: '품번 A' }, { id: 'B200' }, { id: 'reject' }]

  it('선언한 클래스 순서대로 나누고 이미지 없는 클래스도 구획으로 둔다', () => {
    const { groups } = groupByClass(wire, classes)
    expect(groups.map((g) => [g.cls.id, g.items.map((r) => r.ref_id)])).toEqual([
      ['A100', ['r1', 'r2']],
      ['B200', []],
      ['reject', ['r3']],
    ])
  })

  it('선언에서 빠진 클래스의 이미지는 개수만 센다 — 대소문자만 다르면 같은 클래스다', () => {
    expect(groupByClass(wire, classes).orphans).toBe(1)
    const lower = groupByClass([{ ref_id: 'x', label: 0, ng_label: null, class: 'a100' }] as unknown as Reference[], classes)
    expect(lower.groups[0].items).toHaveLength(1)
    expect(lower.orphans).toBe(0)
  })

  it('class 가 없는 행(검사 응답 모양)은 어느 클래스에도 들지 않는다', () => {
    expect(groupByClass([{ ref_id: 'r', label: 0, ng_label: null }] as unknown as Reference[], classes).orphans).toBe(1)
  })
})

describe('emptyClassNotice', () => {
  const classes = [{ id: 'A100', label: '품번 A' }, { id: 'B200' }]
  const full = { cls: classes[0], items: [{ ref_id: 'r1' }] as unknown as Reference[] }
  const empty = { cls: classes[1], items: [] }

  it('비어 있는 클래스를 표시 이름(없으면 id)으로 짚는다', () => {
    expect(emptyClassNotice([full, empty], true)).toContain('B200')
    expect(emptyClassNotice([{ cls: classes[0], items: [] }, empty], true)).toContain('품번 A · B200')
  })

  it('모두 있거나 목록을 아직 못 읽었으면 알리지 않는다', () => {
    expect(emptyClassNotice([full], true)).toBeNull()
    expect(emptyClassNotice([full, empty], false)).toBeNull()
  })
})
