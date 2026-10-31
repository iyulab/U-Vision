import { useState } from 'react'

import { ApiError, runRetention } from '../lib/api'
import type { PinRunResult } from '../hooks/usePin'
import { useUtcToday } from '../hooks/useUtcToday'
import {
  RETENTION_KINDS,
  exportAdvice,
  hasAnythingToDelete,
  kindRows,
  lossLines,
  previousDay,
  samePlanTarget,
  validateRetention,
} from '../lib/retention'
import type { RetentionPurgeResponse, Scenario } from '../lib/types'
import { DataVolumePanel } from './DataVolumePanel'

/**
 * 보존 삭제 — 설치자가 날짜 구간과 부류를 이름으로 골라 정본을 지운다. 엔진은 아무것도 스스로 지우지 않는다.
 * 순서는 서버 계약 그대로다: ① 계획 받기(아무것도 지우지 않는다) → ② 잃는 것을 읽고 확인 → ③ 계획의 지문을 되보내 삭제.
 * 입력이 계획과 달라지면 받아 둔 계획은 쓸 수 없다(화면이 막고, 서버가 지문으로 다시 막는다).
 */
export function RetentionPanel({
  scenarios,
  initialScenarioId,
  runWithPin,
}: {
  scenarios: Scenario[]
  initialScenarioId: string | null
  runWithPin: (action: (pin: string) => Promise<void>) => Promise<PinRunResult>
}) {
  const today = useUtcToday()
  const [scenarioId, setScenarioId] = useState(initialScenarioId ?? scenarios[0]?.scenario_id ?? '')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [kinds, setKinds] = useState<string[]>([])
  const [plan, setPlan] = useState<RetentionPurgeResponse | null>(null)
  const [confirmed, setConfirmed] = useState(false)
  const [done, setDone] = useState<RetentionPurgeResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const request = { from, to, kinds }
  const invalid = validateRetention(request, today)
  // 입력이 계획을 받은 때와 달라졌으면 그 계획은 낡았다 — 상태를 되돌리지 않고 대조로 가린다.
  const livePlan = plan !== null && samePlanTarget(plan, scenarioId, request) ? plan : null
  const canPurge = livePlan !== null && hasAnythingToDelete(livePlan) && confirmed && !busy

  function edit(update: () => void) {
    update()
    setDone(null)
    setError(null)
    setConfirmed(false)
  }

  /** PIN 을 거쳐 서버를 한 번 부르고 응답을 돌려준다(취소 · 불일치는 null, 오류는 화면 오류로). */
  async function call(dryRun: boolean, fingerprint?: string): Promise<RetentionPurgeResponse | null> {
    setBusy(true)
    setError(null)
    let response: RetentionPurgeResponse | null = null
    try {
      const result = await runWithPin(async (pin) => {
        response = await runRetention(
          scenarioId,
          { from, to, kinds, dry_run: dryRun, plan_fingerprint: fingerprint ?? null },
          pin,
        )
      })
      if (result === 'unauthorized') setError('PIN 이 올바르지 않습니다.')
      return result === 'ok' ? response : null
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        // 계획과 삭제 사이에 구간 안이 달라졌다 — 받아 둔 계획을 버리고 다시 받게 한다.
        setPlan(null)
        setError('계획을 받은 뒤 구간 안이 달라졌습니다. 계획을 다시 받아 확인하세요.')
      } else {
        setError(e instanceof Error ? e.message : '요청 실패')
      }
      return null
    } finally {
      setBusy(false)
    }
  }

  async function requestPlan() {
    const response = await call(true)
    if (response) {
      setPlan(response)
      setConfirmed(false)
      setDone(null)
    }
  }

  async function purge() {
    if (!livePlan) return
    const response = await call(false, livePlan.plan_fingerprint)
    if (response) {
      setPlan(null)
      setConfirmed(false)
      setDone(response)
    }
  }

  if (scenarios.length === 0) {
    return <p className="text-sm text-slate-400">시나리오가 없습니다. 먼저 시나리오를 만드세요.</p>
  }

  return (
    <div className="max-w-xl space-y-4">
      <DataVolumePanel />

      <p className="text-sm text-slate-400">
        엔진은 기록을 스스로 지우지 않습니다. 용량이 모자라거나 보존 기간이 끝났을 때 구간을 직접 골라 지웁니다. 먼저 계획만 받아 잃는 것을
        읽고, 그다음에 지웁니다.
      </p>

      <label className="block space-y-1">
        <span className="text-sm text-slate-300">시나리오</span>
        <select
          value={scenarioId}
          onChange={(e) => edit(() => setScenarioId(e.target.value))}
          className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white"
        >
          {scenarios.map((s) => (
            <option key={s.scenario_id} value={s.scenario_id}>
              {s.name}
            </option>
          ))}
        </select>
      </label>

      <div className="grid grid-cols-2 gap-3">
        <label className="block space-y-1">
          <span className="text-sm text-slate-300">시작일 (UTC)</span>
          <input
            type="date"
            value={from}
            max={previousDay(today)}
            onChange={(e) => edit(() => setFrom(e.target.value))}
            className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-sm text-slate-300">종료일 (UTC, 포함)</span>
          <input
            type="date"
            value={to}
            max={previousDay(today)}
            onChange={(e) => edit(() => setTo(e.target.value))}
            className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white"
          />
        </label>
      </div>

      <fieldset className="space-y-2">
        <legend className="text-sm text-slate-300">지울 부류</legend>
        {RETENTION_KINDS.map((k) => (
          <label key={k.kind} className="flex items-start gap-2 text-sm">
            <input
              type="checkbox"
              checked={kinds.includes(k.kind)}
              onChange={(e) =>
                edit(() => setKinds((prev) => (e.target.checked ? [...prev, k.kind] : prev.filter((x) => x !== k.kind))))
              }
              className="mt-1"
            />
            <span>
              <span className="font-medium text-slate-200">{k.label}</span>
              <span className="block text-xs text-slate-500">{k.hint}</span>
            </span>
          </label>
        ))}
        <p className="text-xs text-slate-500">
          판정 기록은 사진 · 레코드 · 라벨을 함께 지웁니다 — 한 건만 지우는 길은 없습니다. 시나리오 · 기준 이미지 · 모델 · 권한 단계는 지우지 않습니다.
        </p>
      </fieldset>

      {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      {(from || to || kinds.length > 0) && invalid && <p className="text-xs text-amber-400">{invalid}</p>}

      <button
        disabled={busy || invalid !== null}
        onClick={() => void requestPlan()}
        className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-semibold text-slate-100 hover:bg-slate-600 disabled:opacity-40"
      >
        계획 받기 (지우지 않음)
      </button>

      {livePlan && (
        <section className="space-y-3 rounded-2xl border border-slate-700 bg-slate-800/50 p-5" aria-labelledby="retention-plan">
          <h2 id="retention-plan" className="text-sm font-semibold text-slate-200">
            계획 — {livePlan.from} ~ {livePlan.to}
          </h2>
          <ul className="space-y-1 text-sm">
            {kindRows(livePlan).map((r) => (
              <li key={r.label}>
                <span className="text-slate-300">{r.label}</span> <span className="text-slate-400">{r.detail}</span>
              </li>
            ))}
          </ul>

          {hasAnythingToDelete(livePlan) ? (
            <>
              {lossLines(livePlan).length > 0 && (
                <div>
                  <p className="text-xs text-slate-400">지우면 잃는 것</p>
                  <ul className="mt-1 list-disc space-y-1 pl-5 text-sm text-slate-200">
                    {lossLines(livePlan).map((line) => (
                      <li key={line}>{line}</li>
                    ))}
                  </ul>
                </div>
              )}
              {exportAdvice(livePlan) && (
                <p className="rounded-lg border border-amber-500/60 px-3 py-2 text-sm text-amber-300">{exportAdvice(livePlan)}</p>
              )}
              <label className="flex items-start gap-2 text-sm">
                <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} className="mt-1" />
                <span>
                  되돌릴 수 없음을 이해했고, 위 계획대로 지웁니다. 큰 구간은 시간이 걸리며 네트워크 공유에서는 더 걸릴 수 있습니다.
                </span>
              </label>
              <button
                disabled={!canPurge}
                onClick={() => void purge()}
                className="rounded-lg bg-red-600 px-4 py-2 text-sm font-semibold text-white hover:bg-red-500 disabled:opacity-40"
              >
                지우기
              </button>
            </>
          ) : (
            <p className="text-sm text-slate-400">이 구간에는 지울 것이 없습니다.</p>
          )}
        </section>
      )}

      {done && (
        <section className="space-y-1 rounded-2xl border border-emerald-600/60 p-5" aria-label="삭제 결과">
          <p className="text-sm font-semibold text-emerald-300">
            {done.purged ? '지웠습니다.' : '끝까지 마치지 못했습니다 — 계획을 다시 받아 남은 것을 확인하세요.'}
          </p>
          <ul className="space-y-1 text-sm text-slate-300">
            {kindRows(done).map((r) => (
              <li key={r.label}>
                {r.label} <span className="text-slate-400">{r.detail}</span>
              </li>
            ))}
          </ul>
          <p className="text-xs text-slate-500">삭제 사실은 시나리오 폴더의 retention.jsonl 에 남았습니다.</p>
        </section>
      )}
    </div>
  )
}
