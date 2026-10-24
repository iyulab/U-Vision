import { describe, expect, it } from 'vitest'
import { splitReferences } from './references'
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
