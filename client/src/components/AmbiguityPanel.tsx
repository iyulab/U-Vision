import { groupLine } from '../lib/ambiguity'
import type { Ambiguity } from '../lib/types'

/**
 * «모르겠음» 비율 — 사람이 문제 큐에서 «모르겠음» 으로 답한 비율. 두 집단을 섞지 않는다: 무작위로 고른 문제의 비율이 시나리오 사진의
 * 모호성을 대표하고, 판정자가 헷갈린 사진을 골라 낸 문제는 어려운 사진에 치우쳐 비율이 높게 나온다. 닫힌 문제가 없으면 부르는 쪽이 그리지 않는다.
 */
export function AmbiguityPanel({ ambiguity }: { ambiguity: Ambiguity }) {
  const min = ambiguity.min_answers_for_rate
  return (
    <section className="rounded-2xl border border-slate-700 bg-slate-800/50 p-5" aria-labelledby="ambiguity-heading">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 id="ambiguity-heading" className="text-sm font-semibold text-slate-200">
          «모르겠음» 비율
        </h2>
        <span className="text-xs text-slate-500">많으면 사진이 흐리거나 클래스 정의가 모호할 수 있습니다 — 판정은 바뀌지 않습니다</span>
      </div>
      <p className="text-sm text-slate-300">
        <span className="text-xs text-slate-400">무작위로 고른 문제</span> {groupLine(ambiguity.representative, min)}
      </p>
      <p className="mt-1 text-sm text-slate-300">
        <span className="text-xs text-slate-400">판정자가 헷갈린 사진을 고른 문제</span> {groupLine(ambiguity.targeted, min)}
        <span className="ml-1 text-xs text-slate-500">(어려운 사진만 골랐으니 비율이 높게 나옵니다 — 시나리오의 모호성이 아닙니다)</span>
      </p>
    </section>
  )
}
