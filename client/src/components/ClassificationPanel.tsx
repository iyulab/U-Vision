import { agreementNotice, agreementSub, confusionTable, predictedRows } from '../lib/classificationMetrics'
import { formatPercent } from '../lib/metrics'
import type { ClassificationMetrics } from '../lib/types'

/**
 * 클래스 판정 — 다중 분류 시나리오의 클래스별 그림: 판정이 클래스를 어떻게 나눴는지(분포), 사람 라벨과 얼마나 맞았는지(일치율),
 * 어느 클래스를 어느 클래스로 헷갈렸는지(혼동 행렬). 사람 라벨이 «정답» 이라 라벨이 붙은 판정만 일치율 · 행렬에 든다 — 라벨러의
 * 편향까지 담긴 값이지 참값이 아니다. 위 NG 재현율 · 오경보는 «꼭 잡을 값» 보기로 따로 계산된다.
 */
export function ClassificationPanel({
  classification,
  declared,
  nameOf,
}: {
  classification: ClassificationMetrics
  /** 시나리오가 선언한 클래스 id 순서 — 행렬의 행 · 열 순서. */
  declared: string[]
  /** 클래스 id → 표시 이름. */
  nameOf: (value: string) => string
}) {
  const notice = agreementNotice(classification)
  const table = confusionTable(classification, declared)
  const distribution = predictedRows(classification)

  return (
    <section
      className="space-y-4 rounded-2xl border border-slate-700 bg-slate-800/50 p-5"
      aria-labelledby="classification-heading"
      data-testid="classification-panel"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="classification-heading" className="text-sm font-semibold text-slate-200">
          클래스 판정
        </h2>
        <span className="text-xs text-slate-500">사람 라벨 대비 — 라벨러의 편향을 담은 값이며 참값이 아닙니다</span>
      </div>
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3">
        <Cell label="판정" value={String(classification.checked)} sub="클래스를 정한 건수" />
        <Cell label="사람 라벨" value={String(classification.labeled)} sub="판정한 건 가운데 라벨이 붙은 건" />
        <Cell
          label="일치율"
          value={formatPercent(classification.agreement_rate ?? null)}
          sub={classification.labeled > 0 ? agreementSub(classification) : '사람 라벨이 붙으면 계산됩니다'}
          warn={classification.labeled > 0 && notice !== null}
        />
      </div>
      {notice && <p className="text-xs text-amber-400">{notice}</p>}
      {distribution.length > 0 && (
        <p className="text-sm text-slate-300" data-testid="predicted-distribution">
          판정 분포 — {distribution.map((d) => `${nameOf(d.id)} ${d.count}`).join(' · ')}
        </p>
      )}
      {table.classes.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm" data-testid="confusion-table">
            <caption className="mb-2 text-left text-xs text-slate-500">
              혼동 행렬 — 행은 사람 라벨(정답), 열은 판정한 클래스. 대각선이 맞게 판정한 건수이고 마지막 열은 그 정답의 재현율입니다.
            </caption>
            <thead className="text-xs text-slate-400">
              <tr>
                <th className="py-1 pr-4 font-medium">정답 ＼ 판정</th>
                {table.classes.map((c) => (
                  <th key={c} className="max-w-[8rem] break-all py-1 pr-4 font-medium">
                    {nameOf(c)}
                  </th>
                ))}
                <th className="whitespace-nowrap py-1 font-medium">재현율</th>
              </tr>
            </thead>
            <tbody>
              {table.rows.map((row, i) => (
                <tr key={row.truth} className="border-t border-slate-700 text-slate-300">
                  <td className="max-w-[8rem] break-all py-1 pr-4 font-medium text-slate-200">{nameOf(row.truth)}</td>
                  {row.cells.map((n, j) => (
                    <td
                      key={table.classes[j]}
                      className={`whitespace-nowrap py-1 pr-4 ${
                        i === j ? 'font-semibold text-emerald-300' : n > 0 ? 'text-amber-400' : 'text-slate-600'
                      }`}
                    >
                      {n}
                    </td>
                  ))}
                  <td className="whitespace-nowrap py-1">{formatPercent(row.recall)}</td>
                </tr>
              ))}
            </tbody>
          </table>
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
