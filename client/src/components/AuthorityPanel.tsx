import { useCallback, useEffect, useState } from 'react'

import { demoteAuthority, getAuthority, promoteAuthority } from '../lib/api'
import type { PinRunResult } from '../hooks/usePin'
import { nextStageUp, stageLabel } from '../lib/authority'
import type { AuthorityState, Scenario } from '../lib/types'

/**
 * 권한 이양 단계 제어(A1) — 관심사 분리: 대시보드는 격상 *신호*(promotion_eligible), 여기서 *변경*.
 * 격상 = 한 단계 위(수동·PIN) / 격하 = 한 단계 아래(안전쪽·PIN). 자동격하는 서버 inspect 인밴드.
 */
export function AuthorityPanel({
  scenarios,
  initialScenarioId,
  runWithPin,
}: {
  scenarios: Scenario[]
  initialScenarioId: string | null
  runWithPin: (action: (pin: string) => Promise<void>) => Promise<PinRunResult>
}) {
  const [scenarioId, setScenarioId] = useState(initialScenarioId ?? scenarios[0]?.scenario_id ?? '')
  const [auth, setAuth] = useState<AuthorityState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    if (!scenarioId) return
    try {
      setAuth(await getAuthority(scenarioId))
    } catch (e) {
      setError(e instanceof Error ? e.message : '권한 단계 로드 실패')
    }
  }, [scenarioId])

  useEffect(() => {
    void load()
  }, [load])

  async function mutate(action: (pin: string) => Promise<void>) {
    setBusy(true)
    setError(null)
    try {
      const result = await runWithPin(action)
      if (result === 'ok') await load()
      else if (result === 'unauthorized') setError('PIN 이 올바르지 않습니다.')
    } catch (e) {
      setError(e instanceof Error ? e.message : '요청 실패')
    } finally {
      setBusy(false)
    }
  }

  const up = auth ? nextStageUp(auth.stage) : null

  return (
    <div className="max-w-xl space-y-4">
      <label className="block space-y-1">
        <span className="text-sm text-slate-300">시나리오</span>
        <select
          value={scenarioId}
          onChange={(e) => setScenarioId(e.target.value)}
          className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white"
        >
          {scenarios.map((s) => (
            <option key={s.scenario_id} value={s.scenario_id}>
              {s.name}
            </option>
          ))}
        </select>
      </label>

      {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      {auth && (
        <div className="space-y-4 rounded-2xl border border-slate-700 bg-slate-800/50 p-5">
          <div>
            <p className="text-xs text-slate-400">현재 권한 단계</p>
            <p className="mt-1 text-lg font-bold text-white">{stageLabel(auth.stage)}</p>
            {auth.reason && <p className="mt-1 text-xs text-slate-500">{auth.reason}</p>}
          </div>

          <div className="flex flex-wrap gap-2">
            {up && (
              <button
                disabled={busy}
                onClick={() =>
                  void mutate((pin) => promoteAuthority(scenarioId, up, pin, 'console'))
                }
                className="rounded-lg bg-emerald-500 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-400 disabled:opacity-40"
              >
                {stageLabel(up)}로 격상
              </button>
            )}
            {auth.stage !== 'shadow' && (
              <button
                disabled={busy}
                onClick={() => void mutate((pin) => demoteAuthority(scenarioId, pin, 'console'))}
                className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-semibold text-slate-200 hover:bg-slate-600 disabled:opacity-40"
              >
                한 단계 격하
              </button>
            )}
          </div>

          <p className="text-xs text-slate-500">
            격상은 데이터 신호(메트릭 탭의 '격상 가능')를 확인한 뒤 사람이 확정합니다. 격하는 ML 열화 시 서버가 자동 수행하기도 합니다.
          </p>

          {auth.history.length > 0 && (
            <details className="text-xs text-slate-400">
              <summary className="cursor-pointer">전이 이력 ({auth.history.length})</summary>
              <ul className="mt-2 space-y-1">
                {auth.history
                  .slice()
                  .reverse()
                  .map((h, i) => (
                    <li key={i}>
                      {h.from} → {h.to} · {h.mode} · {h.at.slice(0, 19)}
                    </li>
                  ))}
              </ul>
            </details>
          )}
        </div>
      )}
    </div>
  )
}
