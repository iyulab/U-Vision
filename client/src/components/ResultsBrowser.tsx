import { useState } from 'react'

import { useResource } from '../hooks/useResource'

import { deleteLabel, listLabels, listResultDates, listResults, putLabel } from '../lib/api'
import { getDeviceId } from '../lib/deviceIdentity'
import { classNameOf, labelMapOf, labelsOf } from '../lib/labels'
import { pendingReviewCount, reviewStateOf } from '../lib/review'
import { percent, summarizeResults } from '../lib/resultSummary'
import { AnomalyImage } from './AnomalyImage'
import { ResultsTable } from './ResultsTable'
import type { Scenario, StoredLabel, StoredResult } from '../lib/types'
import { termOf } from '../lib/verification'
import { csvFileName, downloadCsv, resultsToCsv } from '../lib/csvExport'

/**
 * 결과 조회 — 영속화된 과거 검사 기록을 시나리오·날짜별로 본다(무인증 읽기).
 * 시나리오 select(활성 시나리오 default) → 날짜 select(최신 먼저) → 결과 목록 → 항목 클릭 → 상세.
 */
export function ResultsBrowser({
  scenarios,
  initialScenarioId,
  lockedScenarioId,
  lockedDate,
}: {
  scenarios: Scenario[]
  initialScenarioId: string | null
  /** 지정 시 시나리오를 이것으로 고정하고 시나리오 select 를 숨긴다(운영 당일 목록). */
  lockedScenarioId?: string
  /** 지정 시 날짜를 이것으로 고정하고 날짜 select 를 숨긴다(예: 오늘). */
  lockedDate?: string
}) {
  const [scenarioId, setScenarioId] = useState(
    () => lockedScenarioId ?? initialScenarioId ?? scenarios[0]?.scenario_id ?? '',
  )
  const scenario = scenarios.find((s) => s.scenario_id === scenarioId)
  const scenarioType = scenario?.type
  const nameOf = classNameOf(scenario)
  // 날짜 고정이 아니면 목록에서 고른다. 고른 날짜가 목록에 없으면(시나리오를 바꿨거나 아직 안 골랐으면) 최신 날짜.
  const datesRes = useResource(
    lockedDate || !scenarioId ? null : scenarioId,
    () => listResultDates(scenarioId),
    '날짜 목록 로드 실패',
  )
  const dates = datesRes.data ?? []
  const [pickedDate, setPickedDate] = useState('')
  const date = lockedDate ?? (dates.includes(pickedDate) ? pickedDate : (dates[0] ?? ''))
  const viewKey = scenarioId && date ? `${scenarioId}|${date}` : null
  const view = useResource(
    viewKey,
    () => Promise.all([listResults(scenarioId, date), listLabels(scenarioId, date)]),
    '결과 목록 로드 실패',
  )
  const results: StoredResult[] = view.data?.[0] ?? []
  // 사람 라벨은 낙관적으로 고친다. 고친 목록은 «어느 뷰의 것인가»와 함께 두어, 저장이 늦게 실패해 다시 읽은 옛 뷰의
  // 라벨이 지금 뷰를 덮지 않게 한다.
  const [labelEdits, setLabelEdits] = useState<{ key: string; labels: StoredLabel[] } | null>(null)
  const labels: StoredLabel[] =
    labelEdits !== null && labelEdits.key === viewKey ? labelEdits.labels : (view.data?.[1] ?? [])
  const [selected, setSelected] = useState<StoredResult | null>(null)
  const [reviewOnly, setReviewOnly] = useState(false)
  const [labelError, setLabelError] = useState<string | null>(null)
  const error = labelError ?? datesRes.error ?? view.error
  const loading = view.loading

  function changeScenario(id: string) {
    setScenarioId(id)
    setPickedDate('')
    setLabelEdits(null)
    setLabelError(null)
  }

  function changeDate(d: string) {
    setPickedDate(d)
    setLabelEdits(null)
    setLabelError(null)
  }

  async function handleLabel(imageId: string, label: string | null) {
    if (viewKey === null) return
    const key = viewKey // 호출 시점의 뷰 — 재동기화 가드용
    const others = labels.filter((l) => l.image_id !== imageId)
    // 낙관적 갱신.
    setLabelEdits({
      key,
      labels: label === null ? others : [...others, { image_id: imageId, label, timestamp: new Date().toISOString() }],
    })
    try {
      if (label === null) await deleteLabel(scenarioId, date, imageId)
      else await putLabel(scenarioId, date, imageId, label, getDeviceId())
    } catch (e) {
      // 실패 시 서버 상태로 재동기화 — 뷰가 바뀌었으면 키가 달라 지금 뷰에 보이지 않는다.
      setLabelError(e instanceof Error ? e.message : '라벨 저장 실패')
      listLabels(scenarioId, date)
        .then((l) => setLabelEdits((prev) => (prev === null || prev.key === key ? { key, labels: l } : prev)))
        .catch(() => {})
    }
  }

  if (scenarios.length === 0) {
    return (
      <p className="text-slate-400">
        등록된 시나리오가 없습니다. 시나리오를 만들고 검사를 실행하면 결과가 여기 쌓입니다.
      </p>
    )
  }

  // ③ ML 교차검증이 켜진 레코드가 있을 때만 ④-A 검토 큐를 노출(ML 비활성 = 기존 동작).
  const labelMap = labelMapOf(labels)
  const hasMl = results.some((r) => r.ml != null)
  const pending = pendingReviewCount(results, labelMap)
  // hasMl 가드: ML 비활성 날짜로 전환되면 토글 UI 가 사라지므로(끌 방법 없음) 필터를 무시해
  // 정상 결과가 "검토 대기 없음"에 가려지지 않게 한다.
  const shown =
    reviewOnly && hasMl
      ? results.filter((r) => reviewStateOf(r, labelMap.get(r.image_id)) === 'pending')
      : results

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-3">
        {!lockedScenarioId && (
          <label className="space-y-1">
            <span className="block text-xs text-slate-400">시나리오</span>
            <select
              value={scenarioId}
              onChange={(e) => changeScenario(e.target.value)}
              className="rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-sm text-white focus:border-emerald-400 focus:outline-none"
            >
              {scenarios.map((s) => (
                <option key={s.scenario_id} value={s.scenario_id} className="bg-slate-800">
                  {s.name}
                </option>
              ))}
            </select>
          </label>
        )}
        {!lockedDate && (
          <label className="space-y-1">
            <span className="block text-xs text-slate-400">날짜</span>
            <select
              value={date}
              onChange={(e) => changeDate(e.target.value)}
              disabled={dates.length === 0}
              className="rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-sm text-white focus:border-emerald-400 focus:outline-none disabled:opacity-40"
            >
              {dates.length === 0 ? (
                <option value="">검사 기록 없음</option>
              ) : (
                dates.map((d) => (
                  <option key={d} value={d} className="bg-slate-800">
                    {d}
                  </option>
                ))
              )}
            </select>
          </label>
        )}
        {shown.length > 0 && (
          <button
            onClick={() => downloadCsv(csvFileName(scenarioId, date), resultsToCsv(shown, labelMap, scenario))}
            className="ml-auto self-end rounded-lg bg-slate-700 px-3 py-2 text-sm font-medium text-slate-200 hover:bg-slate-600"
          >
            CSV 내보내기 ({shown.length}건)
          </button>
        )}
      </div>

      {results.length > 0 && !loading && (
        <SummaryStrip results={results} labels={labelMap} scenarioType={scenarioType} nameOf={nameOf} />
      )}

      {/* ④-A 검토 큐 — ③ 2중체크가 불일치/저신뢰로 표면화한 건을 사람이 라벨로 해소한다. */}
      {hasMl && (
        <div className="flex items-center gap-3 text-sm">
          <span
            className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-bold ${
              pending > 0 ? 'bg-amber-500/20 text-amber-300' : 'bg-slate-700 text-slate-400'
            }`}
          >
            검토 대기 {pending}건
          </span>
          <label className="inline-flex items-center gap-2 text-slate-300">
            <input
              type="checkbox"
              checked={reviewOnly}
              onChange={(e) => setReviewOnly(e.target.checked)}
              className="h-4 w-4 accent-amber-500"
            />
            검토 대기만 보기
          </label>
        </div>
      )}

      {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      {loading ? (
        <p className="text-slate-400">불러오는 중…</p>
      ) : results.length === 0 ? (
        <p className="text-slate-400">
          {date ? '이 날짜에 검사 기록이 없습니다.' : '검사 기록이 없습니다.'}
        </p>
      ) : shown.length === 0 ? (
        <p className="text-slate-400">검토 대기 중인 검사가 없습니다.</p>
      ) : (
        <ResultsTable
          scenarioType={scenarioType}
          labelSet={labelsOf(scenario)}
          nameOf={nameOf}
          results={shown}
          labels={labelMap}
          date={date}
          onLabel={handleLabel}
          onSelect={setSelected}
        />
      )}

      {selected && (
        <ResultDetail
          scenarioType={scenarioType}
          nameOf={nameOf}
          result={selected}
          date={date}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  )
}

/** 그날의 판정 요약 — 필터와 무관하게 그날 전부를 센다. */
function SummaryStrip({
  results,
  labels,
  scenarioType,
  nameOf,
}: {
  results: StoredResult[]
  labels: Map<string, StoredLabel>
  scenarioType?: Scenario['type']
  nameOf: (value: string) => string
}) {
  const s = summarizeResults(results, labels)
  return (
    <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm" aria-label="판정 요약">
      <div className="flex gap-2">
        <dt className="text-slate-400">검사</dt>
        <dd className="font-semibold text-white">{s.total}건</dd>
      </div>
      <div className="flex gap-2">
        <dt className="text-slate-400">{termOf('OK', scenarioType)}</dt>
        <dd className="font-semibold text-emerald-300">{s.ok}</dd>
      </div>
      <div className="flex gap-2">
        <dt className="text-slate-400">{termOf('NG', scenarioType)}</dt>
        <dd className="font-semibold text-red-300">
          {s.ng}
          {s.ngRate !== null && <span className="ml-1 font-normal text-slate-400">({percent(s.ngRate)})</span>}
        </dd>
      </div>
      {s.defects.length > 0 && (
        <div className="flex gap-2">
          <dt className="text-slate-400">불량 유형</dt>
          <dd className="text-red-200">{s.defects.map((d) => `${nameOf(d.id)} ${d.count}`).join(' · ')}</dd>
        </div>
      )}
      {s.labeled > 0 && (
        <div className="flex gap-2">
          <dt className="text-slate-400">사람 확인</dt>
          <dd className="font-semibold text-white">
            {s.labeled}건
            <span className={`ml-1 font-normal ${s.corrected > 0 ? 'text-amber-300' : 'text-slate-400'}`}>
              (판정과 다름 {s.corrected})
            </span>
          </dd>
        </div>
      )}
    </dl>
  )
}

function ResultDetail({
  result,
  date,
  onClose,
  scenarioType,
  nameOf,
}: {
  scenarioType?: Scenario['type']
  nameOf: (value: string) => string
  result: StoredResult
  date: string
  onClose: () => void
}) {
  const ng = result.verdict === 'NG'
  return (
    <div
      className="fixed inset-0 z-20 flex items-center justify-center bg-black/70 p-6"
      onClick={onClose}
    >
      <div
        className="max-h-[90vh] w-full max-w-2xl space-y-4 overflow-y-auto rounded-2xl bg-slate-800 p-6 shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between">
          <VerdictPill
            verdict={result.verdict}
            defect={result.class === undefined ? undefined : nameOf(result.class)}
            scenarioType={scenarioType}
            large
          />
          <button
            onClick={onClose}
            className="rounded-lg bg-slate-700 px-3 py-1.5 text-sm font-medium text-slate-200 hover:bg-slate-600"
          >
            닫기
          </button>
        </div>

        <AnomalyImage scenarioId={result.scenario_id} date={date} imageId={result.image_id} />

        {ng && result.findings && (
          <div className="rounded-lg bg-red-600/20 p-3 text-sm leading-snug text-red-200">
            {result.findings}
          </div>
        )}

        <dl className="grid grid-cols-2 gap-2 text-sm">
          <dt className="text-slate-400">신뢰도</dt>
          <dd className="text-right font-mono text-emerald-400">
            {(result.confidence * 100).toFixed(0)}%
          </dd>
          <dt className="text-slate-400">시각</dt>
          <dd className="text-right text-slate-200">{formatDateTime(result.timestamp)}</dd>
          <dt className="text-slate-400">태블릿</dt>
          <dd className="text-right text-slate-200">
            {result.device_label || result.device_id || '—'}
          </dd>
          {result.expected_part && (
            <>
              <dt className="text-slate-400">기대 품번</dt>
              <dd className="min-w-0 break-all text-right font-mono text-slate-200">{result.expected_part}</dd>
              <dt className="text-slate-400">읽은 코드</dt>
              <dd className="min-w-0 break-all text-right font-mono text-slate-200">{result.observed_code ?? '—'}</dd>
            </>
          )}
          <dt className="text-slate-400">이미지 ID</dt>
          <dd className="min-w-0 break-all text-right font-mono text-xs text-slate-400">{result.image_id}</dd>
        </dl>
      </div>
    </div>
  )
}

function VerdictPill({
  verdict,
  defect,
  large,
  scenarioType,
}: {
  verdict: 'OK' | 'NG'
  /** 레코드 클래스의 표시 이름 — 시나리오가 NG 를 유형으로 나눌 때만 있다. */
  defect?: string
  large?: boolean
  scenarioType?: Scenario['type']
}) {
  const ng = verdict === 'NG'
  return (
    <span
      className={`inline-flex items-center rounded-lg font-bold text-white ${
        ng ? 'bg-red-600' : 'bg-emerald-600'
      } ${large ? 'px-4 py-1.5 text-lg' : 'px-2.5 py-1 text-sm'}`}
    >
      {termOf(verdict, scenarioType)}
      {ng && defect !== undefined && <span className="ml-1.5 font-normal">· {defect}</span>}
    </span>
  )
}

function formatDateTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return d.toLocaleString('ko-KR')
}
