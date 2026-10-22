import type { Scenario, ScenarioClass } from './types'

/** 서버 클래스 id 규칙과 같다 — 학습 폴더 이름 · VLM 출력 enum 이 된다. */
const ID = /^[A-Za-z0-9_-]{1,64}$/

/**
 * 관리 화면이 보여 줄 불량 유형 — 검사 시나리오가 OK 밖에 둔 클래스. 이분 판정(`NG` 하나뿐)과 오투입 확인은 빈 목록이다.
 */
export function defectsOf(scenario: Pick<Scenario, 'type' | 'classes'>): string[] {
  if (scenario.type === 'verification' || !scenario.classes?.length) return []
  const defects = scenario.classes.filter((c) => c.id !== 'OK').map((c) => c.id)
  return defects.length === 1 && defects[0] === 'NG' ? [] : defects
}

/** 입력란(쉼표 · 공백 구분)을 유형 id 목록으로. 서버가 거부할 입력은 여기서 사유와 함께 막는다. */
export function parseDefects(text: string): { ids: string[] } | { error: string } {
  const ids = text
    .split(/[,\s]+/)
    .map((s) => s.trim())
    .filter((s) => s !== '')
  const bad = ids.find((id) => !ID.test(id))
  if (bad !== undefined) return { error: `유형 이름은 영문 · 숫자 · '_' · '-' 로 64자까지: ${bad}` }
  if (ids.some((id) => id.toUpperCase() === 'OK')) return { error: 'OK 는 유형이 아니다 — 불량 유형만 적는다' }
  if (new Set(ids.map((id) => id.toLowerCase())).size !== ids.length) return { error: '유형 이름이 겹친다(대소문자 무시)' }
  return { ids }
}

/** 유형 목록 → 시나리오 `classes`. 비면 이분 판정(OK · NG)이다. 불량 유형은 모두 꼭 잡을 값이다. */
export function classesFor(ids: string[]): ScenarioClass[] {
  const defects = ids.length === 0 ? ['NG'] : ids
  return [{ id: 'OK', settleable: true }, ...defects.map((id) => ({ id, must_catch: true }))]
}
