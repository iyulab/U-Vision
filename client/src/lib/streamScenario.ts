import type { Scenario } from './types'

/**
 * 스트림 감시(`type: monitoring`) 시나리오를 읽기 전용으로 한눈에 — 소스(카메라 별칭 · 구역 · 선)와 규칙. 카메라 주소는 시나리오에 없다(별칭만,
 * 실제 주소는 노드 로컬). 이 화면은 정의를 고치지 않는다 — 정의는 API 로 한다(편집기가 지어지기 전까지).
 */
export function streamSummary(s: Scenario): { sources: string[]; rules: string[] } {
  const sources = (s.sources ?? []).map(
    (src) => `${src.id} · 카메라 ${src.camera_ref} · 구역 ${src.zones?.length ?? 0}개 · 선 ${src.lines?.length ?? 0}개`,
  )
  const rules = (s.rules ?? []).map(
    (r) => `${r.id} · ${r.kind === 'alarm' ? '경보' : '개수'} · ${r.class} · 소스 ${r.source}${r.zone ? ` · 구역 ${r.zone}` : ''}${r.line ? ` · 선 ${r.line}` : ''}`,
  )
  return { sources, rules }
}
