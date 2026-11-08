import { useState } from 'react'

import { demoteAuthority, getAuthority, getMetrics, promoteAuthority } from '../lib/api'
import type { PinRunResult } from '../hooks/usePin'
import { useResource } from '../hooks/useResource'
import { EVIDENCE_WINDOW_DAYS, evidenceSummary, needsAcknowledgement, nextStageUp, stageLabel, unverifiedMark } from '../lib/authority'
import type { AuthorityStage } from '../lib/authority'
import type { Scenario } from '../lib/types'

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
  // 스트림 감시(monitoring)는 사진을 판정하지 않아 권한 사다리가 없다 — 서버도 409 로 닫는다. 쓸 수 없는 단추를 보이지 않는다.
  const streamOnly = scenarios.find((s) => s.scenario_id === scenarioId)?.type === 'monitoring'
  const authority = useResource(scenarioId && !streamOnly ? scenarioId : null, () => getAuthority(scenarioId), '권한 단계 로드 실패')
  const auth = authority.data ?? null
  // 격상 근거 — 서버의 창 집계(최근 N일)를 읽기 전용으로 보인다. 격상을 막지는 않는다(확정은 사람).
  const evidence = useResource(
    scenarioId && !streamOnly ? scenarioId : null,
    () => getMetrics(scenarioId, new Date().toISOString().slice(0, 10), EVIDENCE_WINDOW_DAYS),
    '격상 근거 로드 실패',
  )
  const ev = evidence.data ? evidenceSummary(evidence.data) : null
  // 변경 실패는 조회 실패와 따로 둔다 — 다시 읽기가 성공해도 방금 거절된 이유가 지워지지 않게.
  const [actionError, setActionError] = useState<string | null>(null)
  const error = actionError ?? authority.error
  const [busy, setBusy] = useState(false)
  // 근거 없는 격상은 «알고 하는가» 를 한 번 더 묻는다 — 서버도 확인 없이는 409 로 거절한다.
  const [confirming, setConfirming] = useState<AuthorityStage | null>(null)

  async function mutate(action: (pin: string) => Promise<void>) {
    setBusy(true)
    setActionError(null)
    try {
      const result = await runWithPin(action)
      if (result === 'ok') await authority.reload()
      else if (result === 'unauthorized') setActionError('PIN 이 올바르지 않습니다.')
    } catch (e) {
      setActionError(e instanceof Error ? e.message : '요청 실패')
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
          onChange={(e) => {
            setScenarioId(e.target.value)
            setActionError(null)
            setConfirming(null)
          }}
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

      {streamOnly && (
        <p data-testid="stream-only" className="rounded-2xl border border-slate-700 bg-slate-800/50 p-5 text-sm text-slate-300">
          이 시나리오는 스트림 감시(monitoring)라 사진을 판정하지 않습니다 — 권한 단계는 사진 판정 시나리오에만 있습니다. 엣지 노드가
          규칙으로 사건을 냅니다(노드 탭).
        </p>
      )}

      {!streamOnly && auth && (
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
                onClick={() => {
                  if (needsAcknowledgement(auth.stage, up, ev?.signal ?? null)) setConfirming(up)
                  else void mutate((pin) => promoteAuthority(scenarioId, up, pin, 'console'))
                }}
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

          {confirming && (
            <div data-testid="acknowledge-box" className="space-y-3 rounded-xl border border-amber-500/60 bg-amber-950/40 p-4 text-sm">
              <p className="text-amber-200">
                {stageLabel(confirming)}로 올리는데 격상 신호가 «적격» 이 아닙니다(아래 근거). 올리면 라인이 ML 에 더 의존하고, 근거 없이 올렸다는 사실이
                격상 이력에 남습니다. 알고 올리시겠습니까?
              </p>
              <div className="flex gap-2">
                <button
                  disabled={busy}
                  onClick={() => {
                    const target = confirming
                    setConfirming(null)
                    void mutate((pin) => promoteAuthority(scenarioId, target, pin, 'console', undefined, true))
                  }}
                  className="rounded-lg bg-amber-500 px-4 py-2 text-sm font-semibold text-slate-950 hover:bg-amber-400 disabled:opacity-40"
                >
                  근거 없이 격상 확정
                </button>
                <button
                  onClick={() => setConfirming(null)}
                  className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-semibold text-slate-200 hover:bg-slate-600"
                >
                  취소
                </button>
              </div>
            </div>
          )}

          {ev && (
            <div data-testid="promotion-evidence" className="space-y-2 rounded-xl border border-slate-700 bg-slate-900/60 p-4 text-sm">
              <p className="text-xs text-slate-400">격상 근거 — 최근 {EVIDENCE_WINDOW_DAYS}일 창 집계(점추정 · 읽기 전용)</p>
              <dl className="space-y-1">
                {ev.rows.map((r) => (
                  <div key={r.label} className="flex justify-between gap-4">
                    <dt className="text-slate-400">{r.label}</dt>
                    <dd className="font-mono text-slate-200">{r.value}</dd>
                  </div>
                ))}
              </dl>
              <p data-testid="promotion-signal" className={ev.signal === 'eligible' ? 'text-emerald-300' : 'text-amber-300'}>
                {ev.signal === 'eligible' && '격상 가능 신호 — 조건 충족(확정은 사람).'}
                {ev.signal === 'unmet' && '격상 조건 미충족 — 재현율 · 오경보율 · 일치율 중 기준에 못 미칩니다. 근거가 약한 채 격상하면 라인이 ML 에 더 의존합니다.'}
                {ev.signal === 'insufficient' && '표본 부족 — 신호를 낼 만큼 쌓이지 않았습니다(적격이라는 뜻이 아닙니다). 불량 점수가 붙은 판정이 아직 적으면 이 칸도 비어 있습니다.'}
              </p>
              {ev.basis && ev.signal !== 'eligible' && (
                <p data-testid="promotion-basis-note" className="text-xs text-slate-400">
                  신호는 지금 서빙되는 모델 판({ev.basis.model.slice(0, 12)})이 점수 매긴 표본만 읽습니다. 모델을 다시 지었다면 새 판이 표본을 채울 때까지 신호가 비어 있는 것이 정상이고, 위 창 집계(재현율 · 오경보율)는 옛 판의 표본까지 섞여 있습니다.
                </p>
              )}
            </div>
          )}

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
                      {unverifiedMark(h) && (
                        <span data-testid="unverified-mark" className="ml-2 rounded bg-amber-500/20 px-1.5 py-0.5 text-amber-300">
                          {unverifiedMark(h)}
                        </span>
                      )}
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
