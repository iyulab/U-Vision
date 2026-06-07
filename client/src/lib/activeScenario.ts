import type { Scenario } from './types'

/**
 * 운영 화면의 활성 시나리오 선택을 localStorage 에 보존한다.
 *
 * ⚠️ **id 문자열만** 저장한다(시나리오 객체 아님) — 객체는 관리자가 수정하는 순간 stale 된다.
 * 표시용 객체는 항상 live 목록에서 해석한다.
 */
const KEY = 'uvision.activeScenarioId'

export function getStoredScenarioId(): string | null {
  try {
    return localStorage.getItem(KEY)
  } catch {
    return null // private 모드 등 localStorage 불가 — graceful
  }
}

export function setStoredScenarioId(id: string): void {
  try {
    localStorage.setItem(KEY, id)
  } catch {
    /* 저장 실패는 무시 — 세션 내 선택은 호출부 state 가 유지 */
  }
}

export function clearStoredScenarioId(): void {
  try {
    localStorage.removeItem(KEY)
  } catch {
    /* noop */
  }
}

/**
 * 저장된 id 를 live 목록과 대조해 활성 시나리오를 해석한다.
 * - 저장 id 가 목록에 있으면 그것
 * - 없으면(삭제됨/최초 실행) 첫 시나리오로 fallback (목록이 비면 null)
 *
 * 반환된 id 가 저장값과 다르면 호출부가 저장소를 갱신해야 한다(이 함수는 부수효과 없음).
 */
export function resolveActiveScenario(
  scenarios: Scenario[],
  storedId: string | null,
): Scenario | null {
  if (scenarios.length === 0) return null
  return scenarios.find((s) => s.scenario_id === storedId) ?? scenarios[0]
}
