import type { Scenario, ScenarioClass } from './types'

/** 서버 클래스 id 규칙과 같다 — 학습 폴더 이름 · VLM 출력 enum 이 된다. */
const ID = /^[A-Za-z0-9_-]{1,64}$/

/** 서버 표시 이름 규칙과 같다 — 앞뒤 공백 · 제어문자 · ',' · '=' 없이 1~32자(입력 문법 «id=이름, …» 을 흐리지 않게). */
const LABEL = /^(?=\S)[^\p{C},=]{1,32}(?<=\S)$/u

/** 불량 유형 하나 — 키(id)와 현장 말 표시 이름. */
export interface Defect {
  id: string
  label?: string
}

/**
 * 관리 화면이 보여 줄 불량 유형 — 검사 시나리오가 OK 밖에 둔 클래스. 이분 판정(`NG` 하나뿐)과 오투입 확인은 빈 목록이다.
 */
export function defectsOf(scenario: Pick<Scenario, 'type' | 'classes'>): Defect[] {
  if (scenario.type === 'verification' || !scenario.classes?.length) return []
  const defects = scenario.classes
    .filter((c) => c.id !== 'OK')
    .map((c) => (c.label === undefined ? { id: c.id } : { id: c.id, label: c.label }))
  return defects.length === 1 && defects[0].id === 'NG' && defects[0].label === undefined ? [] : defects
}

/** 유형 목록 → 입력란 문자열(`parseDefects` 의 역). */
export function formatDefects(defects: Defect[]): string {
  return defects.map((d) => (d.label === undefined ? d.id : `${d.id}=${d.label}`)).join(', ')
}

/**
 * 입력란(쉼표 · 줄바꿈 구분, 항목은 `id` 또는 `id=표시 이름`)을 유형 목록으로. 표시 이름은 공백을 담을 수 있어 공백으로는
 * 나누지 않는다. 서버가 거부할 입력은 여기서 사유와 함께 막는다.
 */
export function parseDefects(text: string): { defects: Defect[] } | { error: string } {
  const defects: Defect[] = []
  for (const entry of text.split(/[,\n]/).map((s) => s.trim()).filter((s) => s !== '')) {
    const eq = entry.indexOf('=')
    const id = (eq < 0 ? entry : entry.slice(0, eq)).trim()
    const label = eq < 0 ? undefined : entry.slice(eq + 1).trim()
    if (!ID.test(id)) return { error: `유형 id 는 영문 · 숫자 · '_' · '-' 로 64자까지, 여럿은 쉼표로 나눈다: ${id}` }
    if (label !== undefined && !LABEL.test(label))
      return { error: `표시 이름은 1~32자, ',' · '=' 없이: ${entry}` }
    defects.push(label === undefined ? { id } : { id, label })
  }
  if (defects.some((d) => d.id.toUpperCase() === 'OK')) return { error: 'OK 는 유형이 아니다 — 불량 유형만 적는다' }
  if (new Set(defects.map((d) => d.id.toLowerCase())).size !== defects.length)
    return { error: '유형 id 가 겹친다(대소문자 무시)' }
  const labels = defects.flatMap((d) => (d.label === undefined ? [] : [d.label.toLowerCase()]))
  if (new Set(labels).size !== labels.length) return { error: '표시 이름이 겹친다' }
  return { defects }
}

/** 유형 목록 → 시나리오 `classes`. 비면 이분 판정(OK · NG)이다. 불량 유형은 모두 꼭 잡을 값이다. */
export function classesFor(defects: Defect[]): ScenarioClass[] {
  const declared = defects.length === 0 ? [{ id: 'NG' }] : defects
  return [{ id: 'OK', settleable: true }, ...declared.map((d) => ({ ...d, must_catch: true }))]
}
