import { deviceLine } from '../lib/solverTrust'
import type { SolverTrust } from '../lib/types'

/**
 * 풀이자 신뢰 — 정답을 아는 문제(`PlantRate`)로 잰 장치별 정답률. 심은 문제가 나가지 않았으면 부르는 쪽이 그리지 않는다.
 * 장치 단위다: 공용 태블릿이면 한 장치에 여러 사람이 섞여 신뢰도 섞인다(사람 단위는 현장 실측 뒤).
 */
export function SolverTrustPanel({ trust }: { trust: SolverTrust }) {
  return (
    <section className="rounded-2xl border border-slate-700 bg-slate-800/50 p-5" aria-labelledby="solver-trust-heading">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 id="solver-trust-heading" className="text-sm font-semibold text-slate-200">
          풀이자 신뢰
        </h2>
        <span className="text-xs text-slate-500">정답을 아는 문제 {trust.planted}건으로 잰 값 — 판정은 바뀌지 않습니다</span>
      </div>
      <p className="text-sm text-slate-300">{deviceLine(trust, trust.min_answers_for_rate)}</p>
      <ul className="mt-3 space-y-1 text-sm">
        {trust.devices.map((d) => (
          <li key={d.device}>
            <span className="font-mono text-xs text-slate-400">{d.device || '(장치 미식별)'}</span>{' '}
            <span className="text-slate-300">{deviceLine(d, trust.min_answers_for_rate)}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}
