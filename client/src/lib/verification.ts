import type { InspectResult, InspectResultV2, Scenario, ScenarioClass } from './types'

/** 오투입 확인 시나리오인가 — 기대 품번을 받아 결과 v2 경로로 판정한다. */
export function isVerification(scenario: Pick<Scenario, 'type'>): boolean {
  return scenario.type === 'verification'
}

/**
 * 결과 v2 → 운영 화면의 판정 결과. 운영 화면은 OK/NG 축으로 상태를 다루므로 좋은 쪽(OK · 일치)만 OK 에, 불일치 ·
 * 불량 유형은 모두 NG 쪽(놓치면 안 되는 값)에 두고, 표시용 클래스(`class_label`)를 함께 싣는다.
 *
 * 다중 분류(`classes` 를 주면)는 클래스가 값 중립이라 이름으로 선악을 가리지 않는다 — 서버가 저장하는 OK/NG 보기와 같게,
 * 결정된 클래스가 **꼭 잡을 값**이면 NG, 아니면 OK 다(꼭 잡을 값이 없는 시나리오는 늘 OK). 선언에 없는 클래스이거나 답에
 * 클래스가 없으면 NG(안전한 쪽 — 차단). `class_name` 은 시나리오가 붙인 표시 이름(없으면 id).
 */
export function toInspectResult(v2: InspectResultV2, classes?: ScenarioClass[]): InspectResult {
  const decided =
    classes && v2.class !== undefined
      ? classes.find((c) => c.id.toLowerCase() === v2.class!.toLowerCase())
      : undefined
  const verdict = classes
    ? decided && decided.must_catch !== true
      ? 'OK'
      : 'NG'
    : v2.class === 'OK' || v2.class === 'MATCH'
      ? 'OK'
      : 'NG'
  return {
    verdict,
    ...(classes ? { class_name: decided?.label ?? decided?.id ?? v2.class ?? '?' } : {}),
    findings: v2.findings ?? '',
    confidence: v2.confidence ?? 0,
    timestamp: v2.timestamp,
    image_id: v2.decision_id,
    requires_review: v2.requires_review,
    posture: v2.posture,
    contested_by: v2.contested_by,
    class_label: v2.class,
  }
}

/** 판정 배지 문구 — 오투입 확인은 일치/불일치, 검사는 OK/NG. */
export function verdictLabel(result: Pick<InspectResult, 'verdict' | 'class_label' | 'class_name'>): string {
  if (result.class_name !== undefined) return `${result.verdict === 'NG' ? '❌' : '✅'} ${result.class_name}`
  if (result.class_label === 'MATCH') return '✅ 일치'
  if (result.class_label === 'MISMATCH') return '❌ 불일치'
  return result.verdict === 'NG' ? '❌ NG' : '✅ OK'
}

/** 짧은 판정 문구(목록용) — 오투입은 일치/불일치, 검사는 OK/NG. */
export function shortVerdictLabel(result: Pick<InspectResult, 'verdict' | 'class_label' | 'class_name'>): string {
  if (result.class_name !== undefined) return result.class_name
  if (result.class_label === 'MATCH') return '일치'
  if (result.class_label === 'MISMATCH') return '불일치'
  return result.verdict
}

/**
 * 결과 표 · 상세의 판정 알약 문구 — 본문 `text` 와 덧붙이는 `detail`. 검사 · 오투입 확인은 OK/NG(일치/불일치)가 본문이고 NG 가
 * 유형으로 나뉘면 그 유형이 덧붙는다. **다중 분류는 클래스가 본체**라 클래스 이름이 본문이다(저장된 OK/NG 는 «꼭 잡을 값인가»
 * 보기일 뿐이라 알약 색만 정한다). 레코드에 클래스가 없으면(옛 레코드) OK/NG 로 돌아간다.
 */
export function verdictPillText(
  verdict: string,
  decidedClass: string | undefined,
  scenarioType: Scenario['type'] | undefined,
  nameOf: (value: string) => string,
): { text: string; detail?: string } {
  if (scenarioType === 'classification' && decidedClass !== undefined) return { text: nameOf(decidedClass) }
  return {
    text: termOf(verdict, scenarioType),
    ...(verdict === 'NG' && decidedClass !== undefined ? { detail: nameOf(decidedClass) } : {}),
  }
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
