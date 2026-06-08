import { useEffect, useState } from 'react'

import { listResultDates, listResults, resultImageUrl } from '../lib/api'
import type { Scenario, StoredResult } from '../lib/types'

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
  const [dates, setDates] = useState<string[]>([])
  const [date, setDate] = useState(lockedDate ?? '')
  const [results, setResults] = useState<StoredResult[]>([])
  const [selected, setSelected] = useState<StoredResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  // 시나리오 변경 → 날짜 목록 로드(최신 default).
  useEffect(() => {
    if (lockedDate) return // 날짜 고정 — 목록 로드 불필요(date 는 lockedDate 유지)
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
  }, [scenarioId, lockedDate])

  // 시나리오·날짜 변경 → 결과 목록 로드.
  useEffect(() => {
    if (!scenarioId || !date) {
      setResults([])
      return
    }
    let cancelled = false
    setLoading(true)
    setError(null)
    listResults(scenarioId, date)
      .then((r) => {
        if (!cancelled) setResults(r)
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : '결과 목록 로드 실패')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [scenarioId, date])

  if (scenarios.length === 0) {
    return (
      <p className="text-slate-400">
        등록된 시나리오가 없습니다. 시나리오를 만들고 검사를 실행하면 결과가 여기 쌓입니다.
      </p>
    )
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-3">
        {!lockedScenarioId && (
          <label className="space-y-1">
            <span className="block text-xs text-slate-400">시나리오</span>
            <select
              value={scenarioId}
              onChange={(e) => setScenarioId(e.target.value)}
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
              onChange={(e) => setDate(e.target.value)}
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
      </div>

      {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      {loading ? (
        <p className="text-slate-400">불러오는 중…</p>
      ) : results.length === 0 ? (
        <p className="text-slate-400">
          {date ? '이 날짜에 검사 기록이 없습니다.' : '검사 기록이 없습니다.'}
        </p>
      ) : (
        <ul className="space-y-2">
          {results.map((r) => (
            <li key={r.image_id}>
              <button
                onClick={() => setSelected(r)}
                className="flex w-full items-center justify-between rounded-xl bg-slate-800 px-4 py-3 text-left hover:bg-slate-700"
              >
                <span className="flex items-center gap-3">
                  <VerdictPill verdict={r.verdict} />
                  {r.verdict === 'NG' && r.findings && (
                    <span className="line-clamp-1 max-w-md text-sm text-slate-300">
                      {r.findings}
                    </span>
                  )}
                </span>
                <span className="flex shrink-0 items-center gap-4 text-xs text-slate-400">
                  {r.device_label && <span className="text-slate-300">{r.device_label}</span>}
                  <span>신뢰도 {(r.confidence * 100).toFixed(0)}%</span>
                  <span>{formatTime(r.timestamp)}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}

      {selected && (
        <ResultDetail
          result={selected}
          date={date}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  )
}

function ResultDetail({
  result,
  date,
  onClose,
}: {
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
          <VerdictPill verdict={result.verdict} large />
          <button
            onClick={onClose}
            className="rounded-lg bg-slate-700 px-3 py-1.5 text-sm font-medium text-slate-200 hover:bg-slate-600"
          >
            닫기
          </button>
        </div>

        <img
          src={resultImageUrl(result.scenario_id, date, result.image_id)}
          alt="검사 캡처"
          className="w-full rounded-lg bg-black object-contain"
        />

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
          <dt className="text-slate-400">이미지 ID</dt>
          <dd className="text-right font-mono text-xs text-slate-400">{result.image_id}</dd>
        </dl>
      </div>
    </div>
  )
}

function VerdictPill({ verdict, large }: { verdict: 'OK' | 'NG'; large?: boolean }) {
  const ng = verdict === 'NG'
  return (
    <span
      className={`inline-flex items-center rounded-lg font-bold text-white ${
        ng ? 'bg-red-600' : 'bg-emerald-600'
      } ${large ? 'px-4 py-1.5 text-lg' : 'px-2.5 py-1 text-sm'}`}
    >
      {ng ? 'NG' : 'OK'}
    </span>
  )
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return d.toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

function formatDateTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return d.toLocaleString('ko-KR')
}
