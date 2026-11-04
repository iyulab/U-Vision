import { useState } from 'react'

import { useResource } from '../hooks/useResource'
import type { RunWithPin } from '../hooks/usePin'
import { getDecisionEvidence, rejudgeResult } from '../lib/api'
import {
  agreementText,
  evidenceSummary,
  kindText,
  notableAnnotations,
  opinionText,
  outcomeText,
  tierText,
  votesText,
} from '../lib/evidence'
import type { DecisionEvidence, RejudgeResponse } from '../lib/types'

const FAIL = '판정 근거를 읽지 못했습니다'

/** 재판정으로 물을 수 있는 판정원 — 서버가 못 하는 것(오라클 꺼짐 등)은 물어보면 사유를 돌려준다. */
const ASKABLE = ['vlm', 'oracle', 'ml', 'memory'] as const

interface Target {
  scenarioId: string
  date: string
  imageId: string
}

/**
 * 판정 근거 — 이 판정을 «누가 · 어떤 경로로 · 무엇을 근거로» 냈는지. 펼쳤을 때만 서버를 읽는다(결과 상세를 열 때마다
 * 장부를 훑지 않는다). 보기는 읽기 전용이다. `runWithPin` 이 있으면(관리 콘솔) «다시 판정» 조작이 더해진다 — 고른 판정원에게
 * 지금 다시 물어 원 판정과 나란히 보이되, 저장된 판정은 바뀌지 않는다(사람 라벨만이 판정을 바로잡는다).
 */
export function EvidencePanel({
  scenarioId,
  date,
  imageId,
  runWithPin,
}: Target & { runWithPin?: RunWithPin }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="space-y-2">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="rounded-lg bg-slate-700 px-2.5 py-1 text-xs font-medium text-slate-200 hover:bg-slate-600"
      >
        판정 근거 {open ? '접기' : '보기'}
      </button>
      {open && <EvidenceBody target={{ scenarioId, date, imageId }} runWithPin={runWithPin} />}
    </div>
  )
}

function EvidenceBody({ target, runWithPin }: { target: Target; runWithPin?: RunWithPin }) {
  const { scenarioId, date, imageId } = target
  const res = useResource(`${scenarioId}/${date}/${imageId}`, () => getDecisionEvidence(scenarioId, date, imageId), FAIL)
  if (res.loading) return <p className="text-xs text-slate-400">읽는 중…</p>
  if (res.error !== null) return <p className="text-xs text-red-300">{res.error}</p>
  if (res.data === undefined) return null
  if (res.data === null) return <p className="text-xs text-slate-400">판정 기록을 찾지 못했습니다</p>
  return <EvidenceView evidence={res.data} target={target} runWithPin={runWithPin} />
}

function EvidenceView({
  evidence: e,
  target,
  runWithPin,
}: {
  evidence: DecisionEvidence
  target: Target
  runWithPin?: RunWithPin
}) {
  const annotations = notableAnnotations(e.annotations)
  // 이 화면에서 방금 한 재판정 — 서버 기록(e.rejudges) 뒤에 이어 붙인다.
  const [fresh, setFresh] = useState<RejudgeResponse[]>([])
  const rejudges = [...(e.rejudges ?? []), ...fresh]
  return (
    <div className="space-y-3 rounded-lg bg-slate-900/60 p-3 text-sm">
      <p className="text-slate-200">{evidenceSummary(e)}</p>

      {e.path !== undefined && e.path.length > 0 && (
        <ol className="space-y-1">
          {e.path.map((step) => (
            <li key={step.tier} className="flex items-baseline justify-between gap-3">
              <span className="text-slate-300">
                <span className="font-mono">{step.tier}</span>{' '}
                <span className="text-xs text-slate-400">{kindText(step.kind)}</span>
              </span>
              <span className="text-right">
                <span className={step.outcome === 'settled' ? 'font-medium text-emerald-300' : 'text-amber-300'}>
                  {outcomeText(step.outcome)}
                </span>
                {step.detail !== undefined && <span className="ml-2 text-xs text-slate-400">{step.detail}</span>}
              </span>
            </li>
          ))}
        </ol>
      )}

      {e.ml !== undefined && (
        <p className="text-xs text-slate-300">
          전용 ML 의 답 <span className="font-mono text-slate-100">{e.ml.label}</span> (
          {(e.ml.confidence * 100).toFixed(0)}%)
          {e.agreement !== undefined && (
            <span className={e.agreement ? ' text-emerald-300' : ' text-amber-300'}>
              {' '}
              · VLM 과 {e.agreement ? '일치' : '불일치'}
            </span>
          )}
          {e.ml.model_version !== undefined && <span className="text-slate-500"> · 판 {e.ml.model_version}</span>}
        </p>
      )}

      {e.human_label !== undefined && (
        <p className="text-xs text-slate-300">
          사람이 정한 라벨 <span className="font-mono text-slate-100">{e.human_label}</span>
        </p>
      )}
      {e.oracle_opinion !== undefined && (
        <p className="text-xs text-slate-300">
          오라클의 2차 소견 <span className="font-mono text-slate-100">{e.oracle_opinion}</span>
          <span className="text-slate-500"> · 판정을 바꾸지 않는다</span>
        </p>
      )}

      {e.tier_version !== undefined && (
        <p className="break-all text-xs text-slate-500">
          확정한 단의 판 <span className="font-mono">{e.tier_version}</span>
        </p>
      )}

      {annotations.length > 0 && (
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-xs">
          {annotations.map(({ key, value }) => (
            <div key={key} className="contents">
              <dt className="font-mono text-slate-400">{key}</dt>
              <dd className="min-w-0 break-all text-right font-mono text-slate-200">{value}</dd>
            </div>
          ))}
        </dl>
      )}

      {rejudges.length > 0 && (
        <div className="space-y-2 border-t border-slate-700 pt-3">
          <p className="text-xs font-medium text-slate-300">다시 판정한 기록</p>
          {rejudges.map((r) => (
            <RejudgeCard key={r.requested_at} rejudge={r} />
          ))}
        </div>
      )}

      {runWithPin !== undefined && (
        <RejudgeForm target={target} runWithPin={runWithPin} onDone={(r) => setFresh((f) => [...f, r])} />
      )}
    </div>
  )
}

function RejudgeCard({ rejudge: r }: { rejudge: RejudgeResponse }) {
  return (
    <div className="space-y-1 rounded-lg bg-slate-800/80 p-2 text-xs">
      <p className="text-slate-400">{new Date(r.requested_at).toLocaleString('ko-KR')} · 원 판정 {r.original_class}</p>
      <ul className="space-y-0.5">
        {r.opinions.map((o) => (
          <li key={o.tier} className="flex items-baseline justify-between gap-3">
            <span className="text-slate-300">{tierText(o.tier)}</span>
            <span className="text-right">
              <span className={o.status === 'answered' ? 'font-mono text-slate-100' : 'text-slate-400'}>{opinionText(o)}</span>
              {o.differs_from_original === true && <span className="ml-2 text-amber-300">원 판정과 다름</span>}
            </span>
          </li>
        ))}
      </ul>
      <p className="text-slate-200">{agreementText(r.synthesis)}</p>
      <p className="text-slate-500">
        표 <span className="font-mono">{votesText(r.synthesis.votes)}</span>
      </p>
    </div>
  )
}

function RejudgeForm({
  target,
  runWithPin,
  onDone,
}: {
  target: Target
  runWithPin: RunWithPin
  onDone: (r: RejudgeResponse) => void
}) {
  const [picked, setPicked] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggle = (tier: string) =>
    setPicked((p) => (p.includes(tier) ? p.filter((t) => t !== tier) : ASKABLE.filter((t) => t === tier || p.includes(t))))

  async function run() {
    setBusy(true)
    setError(null)
    try {
      let result: RejudgeResponse | undefined
      const outcome = await runWithPin(async (pin) => {
        result = await rejudgeResult(target.scenarioId, target.date, target.imageId, picked, pin)
      })
      if (outcome === 'unauthorized') setError('관리자 PIN 이 맞지 않습니다')
      else if (outcome === 'ok' && result !== undefined) onDone(result)
    } catch (e) {
      setError(e instanceof Error ? e.message : '다시 판정에 실패했습니다')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-2 border-t border-slate-700 pt-3">
      <p className="text-xs text-slate-400">
        고른 판정원에게 지금 다시 물어 원 판정과 나란히 봅니다. 저장된 판정은 바뀌지 않습니다(관리자 PIN · 호출 비용이 듭니다).
      </p>
      <div className="flex flex-wrap gap-x-4 gap-y-1">
        {ASKABLE.map((tier) => (
          <label key={tier} className="flex items-center gap-1.5 text-xs text-slate-200">
            <input type="checkbox" checked={picked.includes(tier)} onChange={() => toggle(tier)} disabled={busy} />
            {tierText(tier)}
          </label>
        ))}
      </div>
      <button
        type="button"
        onClick={() => void run()}
        disabled={busy || picked.length === 0}
        className="rounded-lg bg-slate-700 px-2.5 py-1 text-xs font-medium text-slate-200 hover:bg-slate-600 disabled:opacity-50"
      >
        다시 판정
      </button>
      {busy && <span className="ml-2 text-xs text-slate-400">묻는 중…</span>}
      {error !== null && <p className="text-xs text-red-300">{error}</p>}
    </div>
  )
}
