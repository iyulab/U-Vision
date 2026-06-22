import type { StoredLabel, Verdict } from './types'

/**
 * 허용 라벨 집합 — 이진→다중분류 확장의 단일 seam(클라 측).
 * v1: ['OK','NG']. 다중분류 확장 시 이 배열 소스만 바꾸면 버튼·검증이 따라온다.
 */
export const LABEL_SET = ['OK', 'NG'] as const

export function isValidLabel(label: string): boolean {
  return (LABEL_SET as readonly string[]).includes(label)
}

/** 라벨 배열을 image_id → 라벨 맵으로(표 병합용). */
export function labelMapOf(labels: StoredLabel[]): Map<string, StoredLabel> {
  return new Map(labels.map((l) => [l.image_id, l]))
}

/** VLM 판정과 사람 라벨의 일치 상태(string 비교 — 임의 클래스 집합에 일반적). */
export function agreementOf(
  verdict: Verdict,
  label: string | undefined,
): 'match' | 'mismatch' | 'unlabeled' {
  if (label === undefined) return 'unlabeled'
  return label === verdict ? 'match' : 'mismatch'
}
