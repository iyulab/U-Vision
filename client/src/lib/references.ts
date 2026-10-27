import { REFERENCE_KIND, type Reference } from './types'

/**
 * 검사(inspection) 시나리오에 기준 이미지가 하나도 없으면 알리는 문구 — 없으면 null.
 * 기준이 없는 판정 모델은 정상품을 NG 로 부르는 일이 늘어(오경보) 기준 이미지를 올리는 쪽으로 안내한다.
 * 막지는 않는다: 기준 없이도 저장 · 판정은 된다. 오투입 확인(verification)은 기준 이미지가 아니라
 * 기대 품번을 상대로 하므로 안내하지 않는다. 목록을 아직 못 읽었으면(loaded=false) 깜빡이지 않게 null.
 */
export function missingReferenceNotice(refs: Reference[], loaded: boolean, type: string | undefined): string | null {
  if (!loaded || refs.length > 0) return null
  if ((type ?? 'inspection') !== 'inspection') return null
  return '기준 이미지가 없습니다. 정상(OK) 기준 이미지를 올리면 정상품을 NG 로 잘못 부르는 오경보가 줄어드는 경향이 있습니다.'
}

/** 기준 이미지 목록을 OK · NG 로 나눈다 — wire 의 `label` 은 숫자(0 = ok, 1 = ng)다. */
export function splitReferences(refs: Reference[]): { ok: Reference[]; ng: Reference[] } {
  return {
    ok: refs.filter((r) => REFERENCE_KIND[r.label] === 'ok'),
    ng: refs.filter((r) => REFERENCE_KIND[r.label] === 'ng'),
  }
}
