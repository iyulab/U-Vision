import { REFERENCE_KIND, type Reference } from './types'

/** 기준 이미지 목록을 OK · NG 로 나눈다 — wire 의 `label` 은 숫자(0 = ok, 1 = ng)다. */
export function splitReferences(refs: Reference[]): { ok: Reference[]; ng: Reference[] } {
  return {
    ok: refs.filter((r) => REFERENCE_KIND[r.label] === 'ok'),
    ng: refs.filter((r) => REFERENCE_KIND[r.label] === 'ng'),
  }
}
