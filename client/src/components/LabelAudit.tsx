import { useState } from 'react'

import { useResource } from '../hooks/useResource'

import { listAuditSample, putAudit, resultImageUrl } from '../lib/api'
import { getDeviceId } from '../lib/deviceIdentity'
import { LABEL_SET } from '../lib/labels'
import type { Scenario } from '../lib/types'
import { termOf } from '../lib/verification'
import { auditResultText } from './auditResultText'

/**
 * 라벨 감사(C1) — 블라인드 재라벨 패스. 직전 라벨을 가린 채 표본 이미지를 다시 판정시켜
 * intra-annotator 일관성을 측정한다. 제출 후에만 직전 라벨/결과를 공개한다(블라인드 보장).
 */
export function LabelAudit({
  scenarios,
  initialScenarioId,
}: {
  scenarios: Scenario[]
  initialScenarioId: string | null
}) {
  const [scenarioId, setScenarioId] = useState(
    () => initialScenarioId ?? scenarios[0]?.scenario_id ?? '',
  )
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10))
  const sample = useResource(
    scenarioId && date ? `${scenarioId}|${date}` : null,
    () => listAuditSample(scenarioId, date),
    '감사 표본 로드 실패',
  )
  const queue = sample.data ?? []
  const [idx, setIdx] = useState(0)
  const [result, setResult] = useState<{ status: string; prior_label: string } | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const error = submitError ?? sample.error
  const [busy, setBusy] = useState(false)

  // 시나리오 · 날짜를 바꾸면 새 표본의 처음부터 — 진행 위치와 직전 제출 결과는 이전 표본의 것이다.
  function changeView(next: { scenarioId?: string; date?: string }) {
    if (next.scenarioId !== undefined) setScenarioId(next.scenarioId)
    if (next.date !== undefined) setDate(next.date)
    setIdx(0)
    setResult(null)
    setSubmitError(null)
  }

  const current = queue[idx]

  async function submit(label: string) {
    if (!current || busy) return
    setBusy(true)
    setSubmitError(null)
    try {
      const out = await putAudit(scenarioId, date, current, label, getDeviceId())
      setResult(out) // 제출 후에야 직전 라벨/결과 공개(블라인드)
    } catch (e) {
      setSubmitError(e instanceof Error ? e.message : '감사 제출 실패')
    } finally {
      setBusy(false)
    }
  }

  function next() {
    setResult(null)
    setIdx((i) => i + 1)
  }

  if (scenarios.length === 0) return <p className="text-slate-400">시나리오가 없습니다.</p>

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-3">
        <label className="space-y-1">
          <span className="block text-xs text-slate-400">시나리오</span>
          <select
            value={scenarioId}
            onChange={(e) => changeView({ scenarioId: e.target.value })}
            className="rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-sm text-white"
          >
            {scenarios.map((s) => (
              <option key={s.scenario_id} value={s.scenario_id} className="bg-slate-800">
                {s.name}
              </option>
            ))}
          </select>
        </label>
        <label className="space-y-1">
          <span className="block text-xs text-slate-400">날짜</span>
          <input
            type="date"
            value={date}
            onChange={(e) => changeView({ date: e.target.value })}
            className="rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-sm text-white"
          />
        </label>
      </div>

      {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      {sample.loading ? (
        <p className="text-slate-400">불러오는 중…</p>
      ) : queue.length === 0 ? (
        <p className="text-slate-400">감사할 표본이 없습니다(라벨 부족 또는 전부 감사됨).</p>
      ) : idx >= queue.length ? (
        <p className="text-emerald-400">감사 완료 — {queue.length}건.</p>
      ) : (
        <div className="space-y-3">
          <p className="text-xs text-slate-400">
            {idx + 1} / {queue.length} · 직전 라벨을 보지 말고 다시 판정하세요(블라인드).
          </p>
          <img
            src={resultImageUrl(scenarioId, date, current)}
            alt="감사 대상"
            className="max-h-96 w-full rounded-lg bg-black object-contain"
          />
          {result ? (
            <div className="space-y-2">
              <p className={auditResultText(result.status).ok ? 'text-emerald-400' : 'text-amber-400'}>
                {auditResultText(result.status).text} · 직전 라벨: {result.prior_label}
              </p>
              <button
                onClick={next}
                className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium text-white hover:bg-slate-600"
              >
                다음
              </button>
            </div>
          ) : (
            <div className="flex gap-3">
              {(LABEL_SET as readonly string[]).map((l) => (
                <button
                  key={l}
                  disabled={busy}
                  onClick={() => submit(l)}
                  className={`rounded-lg px-6 py-3 text-lg font-bold text-white disabled:opacity-50 ${
                    l === 'NG' ? 'bg-red-600 hover:bg-red-500' : 'bg-emerald-600 hover:bg-emerald-500'
                  }`}
                >
                  {termOf(l, scenarios.find((s) => s.scenario_id === scenarioId)?.type)}
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
