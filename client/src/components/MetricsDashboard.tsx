import { useEffect, useState } from 'react'

import { getMetrics, listResultDates } from '../lib/api'
import { formatPercent, fraction, hasNoMetricData, recallLead } from '../lib/metrics'
import type { MetricsSummary, Scenario } from '../lib/types'

/**
 * 메트릭 대시보드(신뢰성 플라이휠 B3 소비) — 시나리오·날짜의 2중체크 관측 신호를 본다(무인증 읽기).
 * agreement·검토·ML degrade 율 + **VLM↔ML NG recall 비교**(FW-3 "ML>VLM" 이 운영서 유지되는지의
 * 데이터 근거 = A1 권한이양 입력). 메트릭 날짜 버킷은 결과와 동일하므로 날짜 목록을 공유한다.
 */
export function MetricsDashboard({
  scenarios,
  initialScenarioId,
}: {
  scenarios: Scenario[]
  initialScenarioId: string | null
}) {
  const [scenarioId, setScenarioId] = useState(
    () => initialScenarioId ?? scenarios[0]?.scenario_id ?? '',
  )
  const [dates, setDates] = useState<string[]>([])
  const [date, setDate] = useState('')
  const [summary, setSummary] = useState<MetricsSummary | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  // 시나리오 변경 → 날짜 목록(결과와 공유, 최신 default).
  useEffect(() => {
    if (!scenarioId) {
      setDates([])
      setDate('')
      return
    }
    let cancelled = false
    setError(null)
    listResultDates(scenarioId)
      .then((d) => {
        if (cancelled) return
        setDates(d)
        setDate(d[0] ?? '')
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : '날짜 목록 로드 실패')
      })
    return () => {
      cancelled = true
    }
  }, [scenarioId])

  // 시나리오·날짜 변경 → 메트릭 집계 로드.
  useEffect(() => {
    if (!scenarioId || !date) {
      setSummary(null)
      return
    }
    let cancelled = false
    setLoading(true)
    setError(null)
    getMetrics(scenarioId, date)
      .then((s) => {
        if (!cancelled) setSummary(s)
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : '메트릭 로드 실패')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [scenarioId, date])

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-3">
        <select
          aria-label="시나리오"
          value={scenarioId}
          onChange={(e) => setScenarioId(e.target.value)}
          className="rounded-lg border border-slate-600 bg-slate-800 px-3 py-2 text-sm text-white"
        >
          {scenarios.map((s) => (
            <option key={s.scenario_id} value={s.scenario_id}>
              {s.name}
            </option>
          ))}
        </select>
        <select
          aria-label="날짜"
          value={date}
          onChange={(e) => setDate(e.target.value)}
          disabled={dates.length === 0}
          className="rounded-lg border border-slate-600 bg-slate-800 px-3 py-2 text-sm text-white disabled:opacity-50"
        >
          {dates.length === 0 ? (
            <option value="">기록 없음</option>
          ) : (
            dates.map((d) => (
              <option key={d} value={d}>
                {d}
              </option>
            ))
          )}
        </select>
        {loading && <span className="text-sm text-slate-400">불러오는 중…</span>}
      </div>

      {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      {summary === null ? (
        <p className="text-sm text-slate-400">시나리오·날짜를 선택하세요.</p>
      ) : hasNoMetricData(summary) ? (
        <p className="rounded-xl border border-slate-700 bg-slate-800/50 px-5 py-8 text-center text-sm text-slate-400">
          이 날짜에 ML 2중체크 검사 데이터가 없습니다.
          <br />
          <span className="text-xs text-slate-500">
            (ML 분류기가 비활성[none]이거나 해당 날짜 검사가 없음 — 플라이휠 ① 단계)
          </span>
        </p>
      ) : (
        <Cards summary={summary} />
      )}
    </div>
  )
}

function Cards({ summary }: { summary: MetricsSummary }) {
  const nonDegraded = summary.inspections - summary.ml_degraded
  const lead = recallLead(summary.vlm_ng_recall, summary.ml_ng_recall)

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <Stat label="검사" value={String(summary.inspections)} sub="ML 2중체크 건" />
        <Stat
          label="일치율"
          value={formatPercent(summary.agreement_rate)}
          sub={`${fraction(summary.agreements, nonDegraded)} 일치`}
        />
        <Stat
          label="검토 대기율"
          value={formatPercent(summary.review_rate)}
          sub={`${fraction(summary.reviews_required, nonDegraded)} 검토 필요`}
          warn={summary.reviews_required > 0}
        />
        <Stat
          label="ML degrade"
          value={formatPercent(summary.degrade_rate)}
          sub={`${fraction(summary.ml_degraded, summary.inspections)} 분류 실패`}
          warn={summary.ml_degraded > 0}
        />
        <Stat
          label="판정 불가"
          value={formatPercent(summary.fail_closed_rate)}
          sub={`${fraction(summary.fail_closed, summary.inspections + summary.fail_closed)} fail-closed`}
          warn={summary.fail_closed > 0}
        />
        <Stat
          label="라벨 일관성"
          value={formatPercent(summary.label_consistency_rate)}
          sub={`${summary.label_consistent}/${summary.audited} 감사 · 충돌 ${summary.label_conflicts_open}`}
          warn={summary.label_conflicts_open > 0}
        />
      </div>

      {/* NG recall 비교 — 플라이휠의 핵심 질문: ML 이 VLM 이 놓친 불량을 잡는가. */}
      <div className="rounded-2xl border border-slate-700 bg-slate-800/50 p-5">
        <div className="mb-1 flex items-center justify-between">
          <h2 className="text-sm font-semibold text-slate-200">NG recall (불량 검출률)</h2>
          <span className="text-xs text-slate-500">
            사람 라벨 NG {summary.labeled_ng}건 기준
            {summary.labeled_ng === 0 && ' — 라벨 필요'}
          </span>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <RecallCell
            label="VLM (주 판정)"
            rate={summary.vlm_ng_recall}
            detail={fraction(summary.vlm_ng_hits, summary.labeled_ng)}
            leading={lead === 'vlm'}
          />
          <RecallCell
            label="ML (교차검증)"
            rate={summary.ml_ng_recall}
            detail={fraction(summary.ml_ng_hits, summary.ml_ng_scored)}
            leading={lead === 'ml'}
          />
        </div>
        {summary.promotion_eligible === true && (
          <div className="mt-3 rounded-lg bg-emerald-500/15 px-4 py-2 text-sm font-semibold text-emerald-300">
            ✅ 격상 가능 신호 — ML NG recall ≥ VLM·≥0.95·일치율 ≥0.9 충족(표본 {summary.inspections}). 확정은 관리자.
          </div>
        )}
        {summary.promotion_eligible === false && (
          <p className="mt-3 text-xs text-slate-500">
            격상 조건 미충족(표본 {summary.inspections}) — ML이 VLM 수준의 NG recall·일치율에 아직 못 미칩니다.
          </p>
        )}
      </div>

      <p className="text-xs text-slate-500">
        라벨 진척 {fraction(summary.labeled, summary.inspections)} · 미라벨 검사는 NG recall 분모에서 제외됩니다.
      </p>
    </div>
  )
}

function Stat({
  label,
  value,
  sub,
  warn,
}: {
  label: string
  value: string
  sub: string
  warn?: boolean
}) {
  return (
    <div className="rounded-xl border border-slate-700 bg-slate-800/50 p-4">
      <div className="text-xs font-medium text-slate-400">{label}</div>
      <div className={`mt-1 text-2xl font-bold ${warn ? 'text-amber-400' : 'text-white'}`}>
        {value}
      </div>
      <div className="mt-1 text-xs text-slate-500">{sub}</div>
    </div>
  )
}

function RecallCell({
  label,
  rate,
  detail,
  leading,
}: {
  label: string
  rate: number | null
  detail: string
  leading: boolean
}) {
  return (
    <div
      className={`rounded-xl border p-4 ${
        leading ? 'border-emerald-500/60 bg-emerald-500/10' : 'border-slate-700 bg-slate-900/40'
      }`}
    >
      <div className="text-xs font-medium text-slate-400">{label}</div>
      <div className={`mt-1 text-2xl font-bold ${leading ? 'text-emerald-400' : 'text-white'}`}>
        {formatPercent(rate)}
      </div>
      <div className="mt-1 text-xs text-slate-500">{detail} 검출</div>
    </div>
  )
}
