import type { MlResult } from '../lib/types'

/**
 * 판정 불가의 이유 — 서버가 fail-closed 로 돌려주는 사유(`vlm_unavailable` 등 코드 또는 한국어 문장)를 작업자가 읽는 말로.
 * 모르는 코드는 숨기지 않고 코드를 괄호에 둔다 — «왜 안 되는지» 가 빈 화면이 되지 않게.
 */
export function unavailableReasonText(reason: string | undefined): string {
  switch (reason) {
    case undefined:
    case '':
      return '판정원이 답하지 못했습니다.'
    case 'vlm_unavailable':
      return 'AI 판정 서버(VLM)에 연결하지 못했거나 시간 안에 답이 없었습니다.'
    case 'ml_unavailable':
      return '전용 ML 판정기가 답하지 못했습니다.'
    default:
      return /^[a-z0-9_]+$/.test(reason) ? `판정원이 답하지 못했습니다(${reason}).` : reason
  }
}

/** 판정 불가 패널 문구 — mlHint 있으면 참고 의견 부가(③.5 E2). */
export function unavailableHintText(mlHint: MlResult | undefined): string | null {
  if (!mlHint) return null
  return `ML 참고 의견: ${mlHint.label.toUpperCase()} (${(mlHint.confidence * 100).toFixed(0)}%)`
}
