import { formatBytes } from './dataVolume'
import type { RetentionPurgeResponse } from './types'

/** 서버가 받는 구간 상한(일) — `RetentionService.MaxRangeDays` 와 같다. 넘기면 서버가 400 으로 거절한다. */
export const MAX_RANGE_DAYS = 3660

/** 지울 수 있는 정본 부류 — 서버 `RetentionKinds` 의 이름. 이름에 없는 것은 지우지 않는다. */
export const RETENTION_KINDS = [
  { kind: 'records', label: '판정 기록', hint: '사진 · 판정 레코드 · 사람 라벨 · 이상 지도(날짜 폴더 통째로)' },
  { kind: 'ledger', label: '결정 장부', hint: '판정마다의 단별 답과 정책 근거(날짜 파일 통째로)' },
  { kind: 'metrics', label: '메트릭', hint: '판정마다 한 줄의 관측 기록(날짜 파일 통째로)' },
] as const

const KIND_LABEL: Record<string, string> = Object.fromEntries(RETENTION_KINDS.map((k) => [k.kind, k.label]))

export function kindLabel(kind: string): string {
  return KIND_LABEL[kind] ?? kind
}

export interface RetentionRequest {
  from: string
  to: string
  kinds: string[]
}

const MS_PER_DAY = 86_400_000

function dayNumber(date: string): number {
  return Date.parse(`${date}T00:00:00Z`) / MS_PER_DAY
}

/** `yyyy-MM-dd` 한 날짜의 하루 전. */
export function previousDay(date: string): string {
  return new Date(Date.parse(`${date}T00:00:00Z`) - MS_PER_DAY).toISOString().slice(0, 10)
}

/**
 * 보내기 전에 서버가 거절할 입력을 먼저 잡는다(서버가 같은 규칙으로 다시 검사한다 — 여기는 읽기 쉬운 안내일 뿐).
 * `today` 는 UTC 오늘 — 서버의 날짜 폴더 기준이며, 오늘과 그 뒤는 진행 중인 판정의 쓰기와 겹치므로 구간에 못 든다.
 */
export function validateRetention(req: RetentionRequest, today: string): string | null {
  if (!req.from || !req.to) return '시작일과 종료일을 모두 고르세요.'
  if (req.from > req.to) return '시작일이 종료일보다 늦습니다.'
  if (req.to >= today) return '오늘(UTC)과 그 뒤는 지울 수 없습니다 — 진행 중인 날의 기록을 지키기 위해 어제까지만 고를 수 있습니다.'
  if (dayNumber(req.to) - dayNumber(req.from) + 1 > MAX_RANGE_DAYS) return `한 번에 ${MAX_RANGE_DAYS}일을 넘는 구간은 지울 수 없습니다.`
  if (req.kinds.length === 0) return '지울 부류를 하나 이상 고르세요.'
  return null
}

/** 계획이 가리키는 요청과 지금 입력이 같은가 — 입력이 바뀌면 받아 둔 계획으로는 지울 수 없다. */
export function samePlanTarget(plan: RetentionPurgeResponse, scenarioId: string, req: RetentionRequest): boolean {
  if (plan.scenario_id !== scenarioId || plan.from !== req.from || plan.to !== req.to) return false
  const planned = plan.kinds
    .map((k) => k.kind)
    .sort()
    .join(',')
  return planned === [...req.kinds].sort().join(',')
}

/** 계획이 지울 파일이 하나라도 있는가 — 없으면 지우기 단추를 내지 않는다. */
export function hasAnythingToDelete(plan: RetentionPurgeResponse): boolean {
  return plan.kinds.some((k) => k.files > 0)
}

export interface PlanRow {
  label: string
  detail: string
}

/** 부류별 «날짜 · 파일 · 용량» 줄. */
export function kindRows(plan: RetentionPurgeResponse): PlanRow[] {
  return plan.kinds.map((k) => ({
    label: kindLabel(k.kind),
    detail: `${k.days}일 · 파일 ${k.files.toLocaleString('ko-KR')}개 · ${formatBytes(k.bytes)}`,
  }))
}

/**
 * 지우면 잃는 것 — 고른 부류에서 0 이 아닌 칸만 문장으로. 되돌릴 수 없는 행위라 숫자를 줄여 쓰지 않고, 모두 0 이면 빈 배열.
 */
export function lossLines(plan: RetentionPurgeResponse): string[] {
  const l = plan.lost
  const n = (v: number) => v.toLocaleString('ko-KR')
  const lines: string[] = []
  if (l.results > 0) lines.push(`판정 레코드 ${n(l.results)}건`)
  if (l.human_labels > 0) {
    lines.push(
      l.labels_not_exported > 0
        ? `사람 라벨 ${n(l.human_labels)}건 — 그중 ${n(l.labels_not_exported)}건은 어느 데이터셋 export 에도 담기지 않았습니다`
        : `사람 라벨 ${n(l.human_labels)}건 (모두 데이터셋 export 에 담겨 있습니다)`,
    )
  }
  if (l.open_questions > 0) lines.push(`열린 문제 ${n(l.open_questions)}건 — 사진이 없어져 풀 수 없으므로 철회됩니다`)
  if (l.decisions > 0) lines.push(`결정 장부의 결정 ${n(l.decisions)}건 — 관문 재측정의 원천입니다`)
  if (l.metric_rows > 0) lines.push(`메트릭 ${n(l.metric_rows)}행`)
  return lines
}

/** 사람 라벨이 export 에 모두 담기지 않았을 때의 권고(막지는 않는다). */
export function exportAdvice(plan: RetentionPurgeResponse): string | null {
  return plan.covered_by_export === false
    ? '구간 안 사람 라벨 일부가 데이터셋 export 에 담기지 않았습니다. 지우면 학습 정답이 남는 곳이 없으니 먼저 export 하기를 권합니다.'
    : null
}
