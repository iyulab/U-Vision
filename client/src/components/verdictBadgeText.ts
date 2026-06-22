import type { InspectResult } from '../lib/types'

/**
 * 검토 필요 밴드 문구(③.5 A2 ReviewHold) — requires_review 면 비차단 '검토 필요',
 * ML 불일치면 ML 의견 부가. 정상 자동확정이면 null.
 */
export function reviewBandText(result: InspectResult): string | null {
  if (result.requires_review !== true) return null
  if (result.ml && result.agreement === false)
    return `🔍 검토 필요 · ML 의견: ${result.ml.label.toUpperCase()}`
  return '🔍 검토 필요'
}
