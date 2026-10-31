/**
 * 메트릭 표시 순수 로직(B3 대시보드) — 비율 포맷·NG recall 비교·empty-state 판정.
 * 분모 0(null) 을 "0%" 로 위장하지 않고 "—"(데이터 없음)로 정직히 표시한다.
 */

import type { MetricsSummary } from './types'

/** 비율(0~1, null=undefined) → 퍼센트 문자열. null 이면 '—'(데이터 없음). */
export function formatPercent(rate: number | null, digits = 0): string {
  if (rate === null || Number.isNaN(rate)) return '—'
  return `${(rate * 100).toFixed(digits)}%`
}

/** 분자/분모 카운트 → "n/d" 보조 표기. */
export function fraction(numerator: number, denominator: number): string {
  return `${numerator}/${denominator}`
}

/**
 * 메트릭이 비었을 때 보일 안내. 계수 시나리오는 2중체크 · 재현율 지표를 쓰지 않고 계수 관찰(판정마다 남는 줄에서 센다)만 낸다 —
 * 그 줄이 없는 날은 «지표를 안 쓴다» 가 아니라 «그 날 계수 판정 줄이 없다» 이며, 줄을 남기기 전에 한 판정은 여기 없고 결과 조회에 있다.
 */
export function emptyMetricsNotice(counting: boolean): { lead: string; detail: string } {
  return counting
    ? {
        lead: '이 날짜에 계수 판정 기록이 없습니다.',
        detail: '계수 관찰은 판정마다 남는 지표 줄에서 셉니다 — 그 줄을 남기기 전에 한 판정은 여기 없고, 결과 조회에서 근거 상자와 함께 봅니다.',
      }
    : {
        lead: '이 날짜에 ML 2중체크 검사 데이터가 없습니다.',
        detail: '(ML 분류기가 비활성[none]이거나 해당 날짜 검사가 없음 — 플라이휠 ① 단계)',
      }
}

/** 메트릭 데이터(2중체크 · fail-closed · 계수 관찰)가 한 건도 없는가 — 대시보드 empty-state 판정. */
export function hasNoMetricData(summary: MetricsSummary): boolean {
  return !hasVlmMlMetrics(summary) && !summary.counting
}

/**
 * VLM · ML 집계(검사 · 일치 · 재현율 …)에 읽을 것이 있는가. 계수 판정은 이 집계에 들지 않으므로(서버가 갈라 따로 센다) 계수만 있는
 * 날은 false 이고 이때 이 카드들을 그리면 전부 0 인 칸이 «데이터 있음» 처럼 보인다.
 */
export function hasVlmMlMetrics(summary: MetricsSummary): boolean {
  return summary.inspections + summary.fail_closed > 0
}

/**
 * VLM·ML NG recall 비교 결과 — 대시보드가 우열을 강조(전용 ML 이 VLM 보다 NG 를 잘 잡는지 운영 데이터로 확인).
 * 둘 중 하나라도 측정 불가(null)면 비교 안 함('none').
 */
export type RecallLead = 'ml' | 'vlm' | 'tie' | 'none'

export function recallLead(vlm: number | null, ml: number | null): RecallLead {
  if (vlm === null || ml === null) return 'none'
  if (ml > vlm) return 'ml'
  if (vlm > ml) return 'vlm'
  return 'tie'
}

/**
 * VLM·ML 오경보율(사람이 OK 라 한 것을 불량으로 부른 비율) 비교 — 재현율과 반대로 **낮을수록** 좋다. 재현율만 보면 불량을
 * 많이 부르는 쪽이 좋아 보이므로 한 쌍으로 읽게 하는 짝이다. 하나라도 측정 불가(null)면 비교 안 함('none').
 */
export function falseAlarmLead(vlm: number | null, ml: number | null): RecallLead {
  if (vlm === null || ml === null) return 'none'
  if (ml < vlm) return 'ml'
  if (vlm < ml) return 'vlm'
  return 'tie'
}

/**
 * 감사(블라인드 재라벨)의 불량 쪽 · 정상 쪽 일치 한 줄 — 감사 쌍이 없으면 null. 전체 일관성률은 정상이 압도적일 때 불량 쪽의
 * 갈림을 가리므로(κ 패러독스) 두 클래스를 따로 보인다. 괄호의 쌍 수는 그 일치율의 분모 칸(둘 다 그 클래스인 쌍 + 갈린 쌍)이다.
 */
export function auditAgreementLine(
  s: Pick<
    MetricsSummary,
    | 'audit_ng_ng'
    | 'audit_ng_ok'
    | 'audit_ok_ng'
    | 'audit_ok_ok'
    | 'audit_positive_agreement'
    | 'audit_negative_agreement'
  >,
): string | null {
  const ngSeen = s.audit_ng_ng + s.audit_ng_ok + s.audit_ok_ng
  const okSeen = s.audit_ok_ok + s.audit_ng_ok + s.audit_ok_ng
  if (s.audit_ng_ng + s.audit_ng_ok + s.audit_ok_ng + s.audit_ok_ok === 0) return null
  return `불량 일치 ${formatPercent(s.audit_positive_agreement)} (불량으로 본 쌍 ${ngSeen}) · 정상 일치 ${formatPercent(s.audit_negative_agreement)} (정상으로 본 쌍 ${okSeen})`
}

/**
 * 시험 데이터셋에서 넣은 판정이 집계에 있으면 그 사실 한 줄 — 없으면 null. 전부 주입이면 현장 수치가 아니라는 것을,
 * 일부면 섞여 있다는 것을 말한다.
 */
export function datasetNotice(s: Pick<MetricsSummary, 'inspections' | 'dataset_inspections'>): string | null {
  if (s.dataset_inspections <= 0) return null
  if (s.dataset_inspections >= s.inspections)
    return `검사 ${s.inspections}건 모두 시험 데이터셋에서 넣은 판정입니다 — 현장 촬영 수치가 아닙니다.`
  return `검사 ${s.inspections}건 가운데 ${s.dataset_inspections}건은 시험 데이터셋에서 넣은 판정입니다 — 아래 수치는 현장 촬영과 섞여 있습니다.`
}
