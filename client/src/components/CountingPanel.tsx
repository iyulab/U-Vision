import { compositionLine, countingDatasetNotice, kindRows, unavailableNote } from '../lib/countingMetrics'
import { formatPercent, fraction } from '../lib/metrics'
import type { CountingMetrics } from '../lib/types'

/**
 * 계수 관찰 — 계수 판정에서 정답 없이 읽는 신호(NG 비율 · 흔들림 비율 · 검출기 불능, 구성 확인이면 어느 종류가 자주 모자라거나
 * 넘치는지). 개수가 맞았는지는 재지 않는다: 그건 정수 정답이 있어야 하고 벤치마크의 개수 오차 측정이 잰다. 계수 판정이 없는 날이면
 * 부르는 쪽이 그리지 않는다.
 */
export function CountingPanel({ counting }: { counting: CountingMetrics }) {
  const notice = countingDatasetNotice(counting)
  const unavailable = unavailableNote(counting)
  const composition = compositionLine(counting)
  const rows = kindRows(counting)
  const attempts = counting.checked + counting.unavailable

  return (
    <section className="space-y-4 rounded-2xl border border-slate-700 bg-slate-800/50 p-5" aria-labelledby="counting-heading">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="counting-heading" className="text-sm font-semibold text-slate-200">
          계수 관찰
        </h2>
        <span className="text-xs text-slate-500">정답 없이 보는 신호입니다 — 개수가 맞았는지는 재지 않습니다</span>
      </div>
      {notice && <p className="rounded-lg bg-sky-900/40 px-4 py-2 text-sm text-sky-200">{notice}</p>}
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <Cell label="판정" value={String(counting.checked)} sub={`시도 ${attempts}건 가운데`} />
        <Cell label="NG 비율" value={formatPercent(counting.ng_rate)} sub={`${fraction(counting.ng, counting.checked)} NG`} />
        <Cell
          label="흔들림"
          value={formatPercent(counting.uncertain_rate)}
          sub={`${fraction(counting.uncertain, counting.checked)} 사람 확인 요구`}
          warn={counting.uncertain > 0}
        />
        <Cell
          label="검출기 불능"
          value={formatPercent(counting.unavailable_rate)}
          sub={`${fraction(counting.unavailable, attempts)} 판정 못 함`}
          warn={counting.unavailable > 0}
        />
      </div>
      {unavailable && <p className="text-xs text-amber-400">{unavailable}</p>}
      {composition && (
        <div className="space-y-2">
          <p className="text-sm text-slate-300">{composition}</p>
          {rows.length === 0 ? (
            <p className="text-xs text-slate-500">모자라거나 넘친 종류가 한 번도 없었습니다.</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-left text-sm">
                <caption className="mb-2 text-left text-xs text-slate-500">
                  종류별 빈도 — 그 종류가 모자랐던 건수와 기대보다 많거나 기대에 없는데 검출된 건수. 어긋난 건이 많은 종류가 위에 옵니다.
                </caption>
                <thead className="text-xs text-slate-400">
                  <tr>
                    <th className="py-1 pr-4 font-medium">종류</th>
                    <th className="py-1 pr-4 font-medium">모자람</th>
                    <th className="py-1 font-medium">과잉</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.kind} className="border-t border-slate-700 text-slate-300">
                      <td className="py-1 pr-4">{r.kind}</td>
                      <td className={`py-1 pr-4 ${r.missing > 0 ? 'text-amber-400' : ''}`}>{r.missing}</td>
                      <td className={`py-1 ${r.extra > 0 ? 'text-amber-400' : ''}`}>{r.extra}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </section>
  )
}

function Cell({ label, value, sub, warn }: { label: string; value: string; sub: string; warn?: boolean }) {
  return (
    <div className="rounded-xl border border-slate-700 bg-slate-900/40 p-4">
      <div className="text-xs font-medium text-slate-400">{label}</div>
      <div className={`mt-1 text-2xl font-bold ${warn ? 'text-amber-400' : 'text-white'}`}>{value}</div>
      <div className="mt-1 text-xs text-slate-500">{sub}</div>
    </div>
  )
}
