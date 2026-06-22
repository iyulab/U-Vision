import type { LabelAudit, StoredLabel } from './types'

export type AuditStatus = LabelAudit['status']

/** 라벨의 감사 상태(없으면 unaudited). */
export function auditStatusOf(label: StoredLabel | undefined): AuditStatus {
  return label?.audit?.status ?? 'unaudited'
}

/** 미해소 충돌인가(결과표 '충돌' 배지·해소 동선 게이트). */
export function isConflicted(label: StoredLabel | undefined): boolean {
  return auditStatusOf(label) === 'conflicted'
}

/** 미해소 충돌 건수(큐 배지용). */
export function conflictCount(labels: StoredLabel[]): number {
  return labels.filter((l) => auditStatusOf(l) === 'conflicted').length
}
