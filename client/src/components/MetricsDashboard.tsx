import { useState } from 'react'

import { useResource } from '../hooks/useResource'

import { getMetrics, getTrainingReadiness, listResultDates } from '../lib/api'
import { classNameOf } from '../lib/labels'
import { formatPercent, fraction, hasNoMetricData, recallLead } from '../lib/metrics'
import { readinessLine } from '../lib/training'
import type { MetricsSummary, Scenario } from '../lib/types'

/**
 * 메트릭 대시보드(신뢰성 플라이휠 B3 소비) — 시나리오·날짜의 2중체크 관측 신호를 본다(무인증 읽기).
 * agreement·검토·ML degrade 율 + **VLM↔ML NG recall 비교**(전용 ML 이 VLM 보다 NG 를 잘 잡는지 운영
 * 데이터로 확인 — ML 권한 단계를 올릴 근거). 메트릭 날짜 버킷은 결과와 동일하므로 날짜 목록을 공유한다.
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
  // 날짜 목록은 결과와 공유한다. 고른 날짜가 목록에 없으면(시나리오를 바꿨거나 아직 안 골랐으면) 최신 날짜.
  const datesRes = useResource(scenarioId || null, () => listResultDates(scenarioId), '날짜 목록 로드 실패')
  const dates = datesRes.data ?? []
  const [pickedDate, setPickedDate] = useState('')
  const date = dates.includes(pickedDate) ? pickedDate : (dates[0] ?? '')
  const metrics = useResource(
    scenarioId && date ? `${scenarioId}|${date}` : null,
    () => getMetrics(scenarioId, date),
    '메트릭 로드 실패',
  )
  const summary = metrics.data ?? null
  // 학습 준비는 날짜와 무관하다(시나리오의 라벨 전체) — 실패해도 대시보드는 그대로 보인다.
  const readiness = useResource(scenarioId || null, () => getTrainingReadiness(scenarioId), '학습 준비 조회 실패')
  const error = datesRes.error ?? metrics.error
  const loading = metrics.loading

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-3">
        <select
          aria-label="시나리오"
          value={scenarioId}
          onChange={(e) => {
            setScenarioId(e.target.value)
            setPickedDate('')
          }}
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
          onChange={(e) => setPickedDate(e.target.value)}
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

      {readiness.data && <ReadinessLine
          line={readinessLine(readiness.data, classNameOf(scenarios.find((s) => s.scenario_id === scenarioId)))}
        />}

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

/** 전용 ML 학습 준비 — 할 일이 생기면(빌드 가능 · 재빌드 제안) 눈에 띄게. 실행은 관리자의 몫이다. */
function ReadinessLine({ line }: { line: ReturnType<typeof readinessLine> }) {
  return (
    <p
      className={`rounded-lg px-4 py-2 text-sm ${
        line.tone === 'ready' ? 'bg-emerald-500/15 font-semibold text-emerald-300' : 'bg-slate-800/60 text-slate-400'
      }`}
    >
      학습 준비 · {line.text}
    </p>
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

      {/* 오라클 2차 검증(④-B) — 활동이 있을 때만(none 기본 비활성이면 숨김 = 이전과 동일). */}
      {(summary.oracle_evaluated > 0 || summary.oracle_pending > 0) && (
        <div className="rounded-2xl border border-slate-700 bg-slate-800/50 p-5">
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-slate-200">오라클 2차 검증 (④-B)</h2>
            <span className="text-xs text-slate-500">독립 고성능 모델의 비동기 소견 — 라벨 신뢰성 교차검증</span>
          </div>
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-3">
            <Stat
              label="오라클↔사람 일관성"
              value={formatPercent(summary.oracle_consistency_rate)}
              sub={`${fraction(summary.oracle_evaluated - summary.oracle_conflicts, summary.oracle_evaluated)} 일치`}
              warn={summary.oracle_conflicts > 0}
            />
            <Stat
              label="오라클 커버리지"
              value={formatPercent(summary.oracle_coverage_rate)}
              sub={`${fraction(summary.reviews_required - summary.oracle_pending, summary.reviews_required)} 처리`}
            />
            <Stat
              label="오라클 대기"
              value={String(summary.oracle_pending)}
              sub="검토 큐 미처리(스윕 백로그)"
              warn={summary.oracle_pending > 0}
            />
          </div>
          {summary.oracle_conflicts > 0 && (
            <p className="mt-3 text-xs text-slate-500">
              ⚠ 오라클이 사람 라벨과 {summary.oracle_conflicts}건 불일치 — 계통편향 가능성(기록만, 자동 정정 안 함).
            </p>
          )}
        </div>
      )}

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
