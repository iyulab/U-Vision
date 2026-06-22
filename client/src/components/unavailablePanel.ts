import type { MlResult } from '../lib/types'

/** 판정 불가 패널 문구 — mlHint 있으면 참고 의견 부가(③.5 E2). */
export function unavailableHintText(mlHint: MlResult | undefined): string | null {
  if (!mlHint) return null
  return `ML 참고 의견: ${mlHint.label.toUpperCase()} (${(mlHint.confidence * 100).toFixed(0)}%)`
}
