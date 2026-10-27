import { describe, expect, it } from 'vitest'
import { missingReferenceNotice, splitReferences } from './references'
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
