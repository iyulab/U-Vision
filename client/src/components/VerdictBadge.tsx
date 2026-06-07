import type { InspectionPhase } from '../hooks/useInspection'
import type { InspectResult } from '../lib/types'

/**
 * 판정 결과 대형 배지 — 작업자가 멀리서도 OK/NG 를 즉시 인식하도록.
 * 판정 진행 중에는 진행 표시, 결과가 있으면 배지 + 소견.
 */
export function VerdictBadge({
  result,
  phase,
}: {
  result: InspectResult | null
  phase: InspectionPhase
}) {
  const busy = phase === 'capturing' || phase === 'uploading'

  if (busy) {
    return (
      <div className="absolute inset-x-0 bottom-0 flex justify-center pb-8">
        <div className="rounded-2xl bg-black/70 px-8 py-4 text-lg font-medium text-white backdrop-blur">
          {phase === 'capturing' ? '캡처 중…' : '판정 중…'}
        </div>
      </div>
    )
  }

  if (!result) return null

  const ng = result.verdict === 'NG'
  return (
    <div className="absolute inset-x-0 bottom-0 flex justify-center pb-8">
      <div
        className={`flex max-w-2xl flex-col items-center rounded-2xl px-10 py-6 text-center backdrop-blur ${
          ng ? 'bg-red-600/85' : 'bg-emerald-600/85'
        }`}
      >
        <div className="text-7xl font-black tracking-tight text-white">
          {ng ? '❌ NG' : '✅ OK'}
        </div>
        {ng && result.findings && (
          <p className="mt-3 text-base leading-snug text-white/95">{result.findings}</p>
        )}
        <p className="mt-2 text-sm text-white/80">
          신뢰도 {(result.confidence * 100).toFixed(0)}%
        </p>
      </div>
    </div>
  )
}
