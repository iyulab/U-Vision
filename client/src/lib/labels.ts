import type { Scenario, StoredLabel, StoredResult } from './types'

/**
 * 시나리오가 받는 사람 라벨 — 서버 `EffectiveScenario.Labels` 와 같은 규칙. 불량 유형을 선언한 검사는 클래스 id,
 * 그 밖(이분 판정 검사 · 오투입 확인 · 시나리오를 모름)은 OK/NG.
 */
export function labelsOf(scenario: Pick<Scenario, 'type' | 'classes'> | undefined): string[] {
  if (!scenario || scenario.type === 'verification' || !scenario.classes?.length) return ['OK', 'NG']
  return scenario.classes.map((c) => c.id)
}

/** 레코드가 판정한 클래스 — 불량 유형 시나리오면 `class`, 그 밖은 `verdict` 가 곧 클래스다. */
export function decidedClassOf(r: Pick<StoredResult, 'verdict' | 'class'>): string {
  return r.class ?? r.verdict
}

/** 클래스 이름이 같은가 — ML 은 소문자(`ng`)로 답하기도 해 대소문자를 무시한다(서버 일치 비교와 같다). */
export function sameClass(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase()
}

/** operative(사람) 라벨 = history 최신 mode:'label' 이벤트, 없으면 flat label, 둘 다 없으면 undefined. */
export function operativeLabel(l: StoredLabel | undefined): string | undefined {
  if (!l) return undefined
  const fromHistory = [...(l.history ?? [])].reverse().find((e) => e.mode === 'label')?.label
  return fromHistory ?? l.label
}

/**
 * 오라클(④-B) 최신 2차 소견 = history 최신 mode:'oracle' 이벤트, 없으면 undefined.
 * operative 가 아니다(D2 위계) — operativeLabel 과 대조해 사람 라벨러 계통편향을 표면화한다.
 */
export function latestOracleOpinion(l: StoredLabel | undefined): string | undefined {
  if (!l) return undefined
  return [...(l.history ?? [])].reverse().find((e) => e.mode === 'oracle')?.label
}

/** 라벨 배열을 image_id → 라벨 맵으로(표 병합용). */
export function labelMapOf(labels: StoredLabel[]): Map<string, StoredLabel> {
  return new Map(labels.map((l) => [l.image_id, l]))
}

/** 판정 클래스와 사람 라벨의 일치 상태(문자열 비교 — 임의 클래스 집합에 일반적). */
export function agreementOf(decided: string, label: string | undefined): 'match' | 'mismatch' | 'unlabeled' {
  if (label === undefined) return 'unlabeled'
  return label === decided ? 'match' : 'mismatch'
}
