import { useState } from 'react'

import { issueEdgeToken, listEdgeNodes, revokeEdgeNode, setEdgeNodeScenarios } from '../lib/api'
import type { PinRunResult } from '../hooks/usePin'
import { assignmentChanged, describeNode, sortRows, validateTokenInput, type FlagSeverity } from '../lib/edgeNodes'
import type { EdgeNode, EdgeTokenIssued, Scenario } from '../lib/types'

const SEVERITY_STYLE: Record<FlagSeverity, string> = {
  bad: 'text-red-300',
  warn: 'text-amber-300',
  info: 'text-slate-400',
}

const ROW_BORDER: Record<FlagSeverity | 'none', string> = {
  bad: 'border-red-500/70',
  warn: 'border-amber-500/60',
  info: 'border-slate-700',
  none: 'border-slate-700',
}

type RunWithPin = (action: (pin: string) => Promise<void>) => Promise<PinRunResult>

/**
 * 엣지 노드 — 현장 추론 장비(에이전트)의 목록과 관리. 노드 목록은 관리자만 읽으므로(무인증 읽기가 아니다) 첫 조회에서 PIN 을 받는다.
 * 보이는 것은 서버가 준 값 그대로다: 마지막 연락 · 배정된 시나리오 · 에이전트가 스스로 보고한 대기/버린 크롭/따로 둔 항목 · 시계 어긋남.
 * 판정에는 쓰이지 않는 관찰 화면이며, 하는 일은 셋이다 — 등록 토큰 발급 · 시나리오 배정 · 회수(되돌릴 수 없음).
 */
export function EdgeNodesPanel({ scenarios, runWithPin }: { scenarios: Scenario[]; runWithPin: RunWithPin }) {
  const [nodes, setNodes] = useState<EdgeNode[] | null>(null)
  /** 목록을 읽은 시각 — 「N분 전」 계산의 기준(렌더마다 시계를 읽지 않는다). */
  const [loadedAt, setLoadedAt] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [tokenName, setTokenName] = useState('')
  const [ttl, setTtl] = useState(60)
  const [issued, setIssued] = useState<EdgeTokenIssued | null>(null)

  async function guarded(action: (pin: string) => Promise<void>): Promise<boolean> {
    setBusy(true)
    setError(null)
    try {
      const result = await runWithPin(action)
      if (result === 'unauthorized') setError('PIN 이 올바르지 않습니다.')
      return result === 'ok'
    } catch (e) {
      setError(e instanceof Error ? e.message : '요청 실패')
      return false
    } finally {
      setBusy(false)
    }
  }

  async function refresh(): Promise<boolean> {
    return guarded(async (pin) => {
      const list = await listEdgeNodes(pin)
      setNodes(list)
      setLoadedAt(Date.now())
    })
  }

  async function issue() {
    const ok = await guarded(async (pin) => {
      const result = await issueEdgeToken({ name: tokenName.trim() || undefined, ttl_minutes: ttl }, pin)
      setIssued(result)
    })
    if (ok) setTokenName('')
  }

  async function assign(nodeId: string, selected: string[]): Promise<boolean> {
    const ok = await guarded(async (pin) => {
      await setEdgeNodeScenarios(nodeId, selected, pin)
    })
    if (ok) await refresh()
    return ok
  }

  async function revoke(nodeId: string): Promise<void> {
    const ok = await guarded(async (pin) => {
      await revokeEdgeNode(nodeId, pin)
    })
    if (ok) await refresh()
  }

  const tokenInvalid = validateTokenInput(tokenName, ttl)
  const rows = nodes ? sortRows(nodes.map((n) => describeNode(n, loadedAt))) : []

  return (
    <div className="max-w-3xl space-y-5">
      <p className="text-sm text-slate-400">
        현장의 추론 장비(에이전트)가 엔진에 나가는 방향으로만 연결합니다. 이 화면은 노드 목록을 읽고, 등록 토큰을 발급하고, 노드에
        시나리오를 배정하고, 노드를 회수합니다. 판정에는 쓰이지 않습니다.
      </p>

      {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      <section className="space-y-3 rounded-2xl border border-slate-700 bg-slate-800/50 p-5" aria-labelledby="edge-token">
        <h2 id="edge-token" className="text-sm font-semibold text-slate-200">
          등록 토큰 발급
        </h2>
        <p className="text-xs text-slate-500">
          새 노드를 등록할 때 에이전트에 한 번 넣는 토큰입니다. 한 번만 쓸 수 있고, 평문은 발급 직후 이 화면에서만 보입니다.
        </p>
        <div className="grid grid-cols-3 gap-3">
          <label className="col-span-2 block space-y-1">
            <span className="text-sm text-slate-300">노드 이름 (선택)</span>
            <input
              value={tokenName}
              onChange={(e) => setTokenName(e.target.value)}
              placeholder="예: 3라인 젯슨"
              className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white"
            />
          </label>
          <label className="block space-y-1">
            <span className="text-sm text-slate-300">유효 시간 (분)</span>
            <input
              type="number"
              min={1}
              max={1440}
              value={ttl}
              onChange={(e) => setTtl(Number(e.target.value))}
              className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white"
            />
          </label>
        </div>
        {tokenInvalid && <p className="text-xs text-amber-400">{tokenInvalid}</p>}
        <button
          disabled={busy || tokenInvalid !== null}
          onClick={() => void issue()}
          className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-semibold text-slate-100 hover:bg-slate-600 disabled:opacity-40"
        >
          토큰 발급
        </button>
        {issued && (
          <div className="space-y-1 rounded-lg border border-emerald-600/60 p-3" role="status">
            <p className="text-xs text-emerald-300">발급했습니다. 이 화면을 벗어나면 다시 볼 수 없습니다.</p>
            <code className="block break-all rounded bg-slate-900 px-2 py-1 text-sm text-slate-100" data-testid="issued-token">
              {issued.token}
            </code>
            <p className="text-xs text-slate-500">만료 {new Date(issued.expires_at).toLocaleString('ko-KR')}</p>
          </div>
        )}
      </section>

      <section className="space-y-3" aria-labelledby="edge-nodes">
        <div className="flex items-center justify-between gap-3">
          <h2 id="edge-nodes" className="text-sm font-semibold text-slate-200">
            노드 {nodes ? `(${nodes.length})` : ''}
          </h2>
          <button
            disabled={busy}
            onClick={() => void refresh()}
            className="rounded-lg bg-slate-700 px-3 py-1.5 text-sm font-medium text-slate-100 hover:bg-slate-600 disabled:opacity-40"
          >
            {nodes ? '새로고침' : '노드 불러오기'}
          </button>
        </div>

        {nodes === null ? (
          <p className="text-sm text-slate-500">노드 목록은 관리자만 읽습니다 — 불러오기를 누르면 PIN 을 묻습니다.</p>
        ) : rows.length === 0 ? (
          <p className="text-sm text-slate-400">등록된 노드가 없습니다. 위에서 토큰을 발급해 에이전트를 등록하세요.</p>
        ) : (
          <ul className="space-y-3">
            {rows.map((row) => (
              <NodeCard key={row.node.node_id} row={row} scenarios={scenarios} busy={busy} onAssign={assign} onRevoke={revoke} />
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}

function NodeCard({
  row,
  scenarios,
  busy,
  onAssign,
  onRevoke,
}: {
  row: ReturnType<typeof describeNode>
  scenarios: Scenario[]
  busy: boolean
  onAssign: (nodeId: string, selected: string[]) => Promise<boolean>
  onRevoke: (nodeId: string) => Promise<void>
}) {
  const { node } = row
  const [editing, setEditing] = useState(false)
  const [selected, setSelected] = useState<string[]>(node.scenarios)
  const [confirmRevoke, setConfirmRevoke] = useState(false)
  const nameOf = (id: string) => scenarios.find((s) => s.scenario_id === id)?.name ?? id

  return (
    <li
      className={`space-y-2 rounded-2xl border bg-slate-800/50 p-4 ${ROW_BORDER[row.worst ?? 'none']} ${row.revoked ? 'opacity-60' : ''}`}
      aria-label={`노드 ${node.name}`}
    >
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <span className="font-semibold text-slate-100">{node.name}</span>{' '}
          <span className="text-xs text-slate-500">{node.node_id}</span>
        </div>
        <span className="text-xs text-slate-400">{row.lastSeenText}</span>
      </div>

      {row.backlogText && <p className="text-sm text-slate-300">{row.backlogText}</p>}
      {node.status?.agent_version && <p className="text-xs text-slate-500">에이전트 {node.status.agent_version}</p>}

      <p className="text-sm text-slate-300">
        배정:{' '}
        {node.scenarios.length === 0 ? (
          <span className="text-slate-500">없음</span>
        ) : (
          node.scenarios.map(nameOf).join(' · ')
        )}
      </p>

      {row.flags.length > 0 && (
        <ul className="space-y-0.5 text-sm">
          {row.flags.map((f) => (
            <li key={f.text} className={SEVERITY_STYLE[f.severity]}>
              {f.text}
            </li>
          ))}
        </ul>
      )}

      {!row.revoked && (
        <div className="space-y-2 pt-1">
          {editing ? (
            <fieldset className="space-y-1 rounded-lg border border-slate-700 p-3">
              <legend className="px-1 text-xs text-slate-400">배정할 시나리오 (저장하면 전체 교체)</legend>
              {scenarios.length === 0 && <p className="text-xs text-slate-500">시나리오가 없습니다.</p>}
              {scenarios.map((s) => (
                <label key={s.scenario_id} className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={selected.includes(s.scenario_id)}
                    onChange={(e) =>
                      setSelected((prev) => (e.target.checked ? [...prev, s.scenario_id] : prev.filter((x) => x !== s.scenario_id)))
                    }
                  />
                  <span>{s.name}</span>
                </label>
              ))}
              <div className="flex gap-2 pt-1">
                <button
                  disabled={busy || !assignmentChanged(node.scenarios, selected)}
                  onClick={() => void onAssign(node.node_id, selected).then((ok) => ok && setEditing(false))}
                  className="rounded-lg bg-slate-700 px-3 py-1.5 text-sm font-semibold text-slate-100 hover:bg-slate-600 disabled:opacity-40"
                >
                  배정 저장
                </button>
                <button
                  onClick={() => {
                    setSelected(node.scenarios)
                    setEditing(false)
                  }}
                  className="rounded-lg px-3 py-1.5 text-sm text-slate-400 hover:text-slate-200"
                >
                  취소
                </button>
              </div>
            </fieldset>
          ) : (
            <div className="flex flex-wrap gap-2">
              <button
                onClick={() => {
                  setSelected(node.scenarios)
                  setEditing(true)
                }}
                className="rounded-lg bg-slate-700 px-3 py-1.5 text-sm text-slate-100 hover:bg-slate-600"
              >
                배정 편집
              </button>
              {confirmRevoke ? (
                <>
                  <button
                    disabled={busy}
                    onClick={() => void onRevoke(node.node_id)}
                    className="rounded-lg bg-red-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-red-500 disabled:opacity-40"
                  >
                    회수 확정 — 되돌릴 수 없음
                  </button>
                  <button onClick={() => setConfirmRevoke(false)} className="rounded-lg px-3 py-1.5 text-sm text-slate-400 hover:text-slate-200">
                    취소
                  </button>
                </>
              ) : (
                <button
                  onClick={() => setConfirmRevoke(true)}
                  className="rounded-lg px-3 py-1.5 text-sm text-red-300 hover:bg-red-900/30"
                >
                  회수
                </button>
              )}
            </div>
          )}
        </div>
      )}
    </li>
  )
}
