import type { InspectResult, InspectResultV2, Scenario } from './types'

/** 오투입 확인 시나리오인가 — 기대 품번을 받아 결과 v2 경로로 판정한다. */
export function isVerification(scenario: Pick<Scenario, 'type'>): boolean {
  return scenario.type === 'verification'
}

/**
 * 결과 v2 → 운영 화면의 판정 결과. 운영 화면은 OK/NG 축으로 상태를 다루므로 좋은 쪽(OK · 일치)만 OK 에, 불일치 ·
 * 불량 유형은 모두 NG 쪽(놓치면 안 되는 값)에 두고, 표시용 클래스(`class_label`)를 함께 싣는다.
 */
export function toInspectResult(v2: InspectResultV2): InspectResult {
  return {
    verdict: v2.class === 'OK' || v2.class === 'MATCH' ? 'OK' : 'NG',
    findings: v2.findings ?? '',
    confidence: v2.confidence ?? 0,
    timestamp: v2.timestamp,
    image_id: v2.decision_id,
    requires_review: v2.requires_review,
    class_label: v2.class,
  }
}

/** 판정 배지 문구 — 오투입 확인은 일치/불일치, 검사는 OK/NG. */
export function verdictLabel(result: Pick<InspectResult, 'verdict' | 'class_label'>): string {
  if (result.class_label === 'MATCH') return '✅ 일치'
  if (result.class_label === 'MISMATCH') return '❌ 불일치'
  return result.verdict === 'NG' ? '❌ NG' : '✅ OK'
}

/** 짧은 판정 문구(목록용) — 오투입은 일치/불일치, 검사는 OK/NG. */
export function shortVerdictLabel(result: Pick<InspectResult, 'verdict' | 'class_label'>): string {
  if (result.class_label === 'MATCH') return '일치'
  if (result.class_label === 'MISMATCH') return '불일치'
  return result.verdict
}

/**
 * 저장된 OK/NG 값(판정 · 사람 라벨)을 시나리오 유형의 말로 — 오투입 확인은 일치/불일치. 저장값은 바꾸지 않는다:
 * 라벨 · 일치 판정 · 메트릭은 계속 OK/NG 축으로 돈다.
 */
export function termOf(value: string, scenarioType?: Scenario['type']): string {
  if (scenarioType !== 'verification') return value
  if (value === 'OK') return '일치'
  if (value === 'NG') return '불일치'
  return value
}
