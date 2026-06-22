import { useEffect, useRef, useState } from 'react'

import { deleteLabel, listLabels, listResultDates, listResults, putLabel, resultImageUrl } from '../lib/api'
import { labelMapOf } from '../lib/labels'
import { pendingReviewCount, reviewStateOf } from '../lib/review'
import { ResultsTable } from './ResultsTable'
import type { Scenario, StoredLabel, StoredResult } from '../lib/types'

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
  const [labels, setLabels] = useState<StoredLabel[]>([])
  const [selected, setSelected] = useState<StoredResult | null>(null)
  const [reviewOnly, setReviewOnly] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  // 현재 보고 있는 뷰(시나리오·날짜) 키 — 비동기 재동기화가 뷰 전환 후 stale 데이터를
  // 현재 뷰에 덮어쓰지 않도록 가드한다(handleLabel 에러 경로).
  const viewKeyRef = useRef('')
  viewKeyRef.current = `${scenarioId}|${date}`

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

  // 시나리오·날짜 변경 → 결과 + 라벨 목록 로드.
  useEffect(() => {
    if (!scenarioId || !date) {
      setResults([])
      setLabels([])
      return
    }
    let cancelled = false
    setLoading(true)
    setError(null)
    Promise.all([listResults(scenarioId, date), listLabels(scenarioId, date)])
      .then(([r, l]) => {
        if (cancelled) return
        setResults(r)
        setLabels(l)
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

  async function handleLabel(imageId: string, label: string | null) {
    const viewKey = `${scenarioId}|${date}` // 호출 시점의 뷰 — 재동기화 가드용
    // 낙관적 갱신.
    setLabels((prev) => {
      const others = prev.filter((l) => l.image_id !== imageId)
      return label === null
        ? others
        : [...others, { image_id: imageId, label, timestamp: new Date().toISOString() }]
    })
    try {
      if (label === null) await deleteLabel(scenarioId, date, imageId)
      else await putLabel(scenarioId, date, imageId, label)
    } catch (e) {
      // 실패 시 서버 상태로 재동기화 — 단, 그 사이 뷰가 바뀌었으면 적용하지 않는다
      // (옛 뷰의 라벨이 현재 뷰를 덮어쓰는 레이스 방지).
      setError(e instanceof Error ? e.message : '라벨 저장 실패')
      listLabels(scenarioId, date)
        .then((l) => {
          if (viewKeyRef.current === viewKey) setLabels(l)
        })
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
          results={shown}
          labels={labelMap}
          date={date}
          onLabel={handleLabel}
          onSelect={setSelected}
        />
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

function formatDateTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return d.toLocaleString('ko-KR')
}
