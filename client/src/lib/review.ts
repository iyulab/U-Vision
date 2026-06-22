import type { StoredLabel, StoredResult } from './types'

/**
 * 검토 상태 — 신뢰성 플라이휠 ④-A HITL 검토 큐.
 *
 * ③ 2중체크가 `requires_review` 로 불일치/저신뢰를 표면화하면, 사람이 라벨(cycle-37 seam)을
 * 달아 해소한다. 검토 행위 = 라벨링이므로 별도 "검토 완료" 저장 없이 라벨 유무로 큐가 닫힌다.
 *
 * - `auto`: ③가 검토 불요(일치+충분신뢰) 판정 또는 ML 비활성(`requires_review` 부재) → 자동확정.
 * - `pending`: ③가 검토 필요(`requires_review=true`) 판정 + 아직 사람 라벨 없음 → 검토 대기.
 * - `reviewed`: 검토 필요였으나 사람이 라벨을 달아 해소 → 검토 완료.
 */
export type ReviewState = 'auto' | 'pending' | 'reviewed'

export function reviewStateOf(result: StoredResult, label: StoredLabel | undefined): ReviewState {
  if (result.requires_review !== true) return 'auto'
  return label === undefined ? 'pending' : 'reviewed'
}

/** 검토 대기(미해소) 건수 — 큐 배지·필터용. */
export function pendingReviewCount(
  results: StoredResult[],
  labels: Map<string, StoredLabel>,
): number {
  return results.filter((r) => reviewStateOf(r, labels.get(r.image_id)) === 'pending').length
}
