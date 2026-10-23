import type { InspectResult } from '../lib/types'

/**
 * 검토 필요 밴드 문구(③.5 A2 ReviewHold) — requires_review 면 비차단 '검토 필요',
 * 대조 단과 맞서면 그 사실을, ML 불일치면 ML 의견을 부가. 정상 자동확정이면 null.
 */
export function reviewBandText(result: InspectResult): string | null {
  if (result.requires_review !== true) return null
  // 대조 단이 잡은 불일치를 위 단이 뒤집음 — 무엇과 맞서는지 보여야 작업자가 확인할 수 있다.
  if (result.contested_by?.some((c) => c.tier === 'match'))
    return '🔍 검토 필요 · 읽은 코드가 기대 품번과 다릅니다'
  if (result.ml && result.agreement === false)
    return `🔍 검토 필요 · ML 의견: ${result.ml.label.toUpperCase()}`
  return '🔍 검토 필요'
}
