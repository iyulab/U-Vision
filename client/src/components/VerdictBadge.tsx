import type { InspectionPhase } from '../hooks/useInspection'
import type { InspectResult } from '../lib/types'
import { verdictLabel } from '../lib/verification'
import { reviewBandText } from './verdictBadgeText'

/**
 * 판정 결과 대형 배지 — 작업자가 멀리서도 OK/NG 를 즉시 인식하도록.
 * 판정 진행 중에는 진행 표시, 결과가 있으면 배지 + 소견.
 */
export function VerdictBadge({
  result,
  phase,
  aboveShutter = false,
}: {
  result: InspectResult | null
  phase: InspectionPhase
  /** 수동 모드 — 화면 아래 셔터 버튼 위로 올린다(겹치면 소견 · 신뢰도가 가려진다). */
  aboveShutter?: boolean
}) {
  const busy = phase === 'capturing' || phase === 'uploading'
  const bottom = aboveShutter ? 'pb-32' : 'pb-8'

  if (busy) {
    return (
      <div className={`absolute inset-x-0 bottom-0 flex justify-center ${bottom}`}>
        <div className="rounded-2xl bg-black/70 px-8 py-4 text-lg font-medium text-white backdrop-blur">
          {phase === 'capturing' ? '캡처 중…' : '판정 중…'}
        </div>
      </div>
    )
  }

  if (phase === 'unavailable') return null

  if (phase === 'queued') {
    return (
      <div className={`absolute inset-x-0 bottom-0 flex justify-center ${bottom}`}>
        <div className="flex flex-col items-center rounded-2xl bg-slate-700/90 px-8 py-4 text-center text-white backdrop-blur">
          <span className="text-lg font-semibold">오프라인 — 캡처 저장됨</span>
          <span className="mt-1 text-sm text-white/80">연결되면 자동으로 판정합니다. 다음 품목을 계속하세요.</span>
        </div>
      </div>
    )
  }

  if (!result) return null

  const ng = result.verdict === 'NG'
  return (
    <div className={`absolute inset-x-0 bottom-0 flex justify-center ${bottom}`}>
      <div
        className={`flex max-w-2xl flex-col items-center rounded-2xl px-10 py-6 text-center backdrop-blur ${
          ng ? 'bg-red-600/85' : 'bg-emerald-600/85'
        }`}
      >
        <div className="text-7xl font-black tracking-tight text-white">
          {verdictLabel(result)}
        </div>
        {ng && result.findings && (
          <p className="mt-3 text-base leading-snug text-white/95">{result.findings}</p>
        )}
        <p className="mt-2 text-sm text-white/80">
          신뢰도 {(result.confidence * 100).toFixed(0)}%
        </p>
        {reviewBandText(result) && (
          <p className="mt-3 rounded-full bg-amber-500/90 px-4 py-1 text-sm font-semibold text-white">
            {reviewBandText(result)}
          </p>
        )}
      </div>
    </div>
  )
}
