import { useState } from 'react'

import { useResource } from '../hooks/useResource'
import { getDecisionEvidence } from '../lib/api'
import { evidenceSummary, kindText, notableAnnotations, outcomeText } from '../lib/evidence'
import type { DecisionEvidence } from '../lib/types'

const FAIL = '판정 근거를 읽지 못했습니다'

/**
 * 판정 근거 — 이 판정을 «누가 · 어떤 경로로 · 무엇을 근거로» 냈는지. 펼쳤을 때만 서버를 읽는다(결과 상세를 열 때마다
 * 장부를 훑지 않는다). 읽기 전용이다: 판정원을 다시 부르거나 판정을 바꾸지 않는다.
 */
export function EvidencePanel({ scenarioId, date, imageId }: { scenarioId: string; date: string; imageId: string }) {
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
      {open && <EvidenceBody scenarioId={scenarioId} date={date} imageId={imageId} />}
    </div>
  )
}

function EvidenceBody({ scenarioId, date, imageId }: { scenarioId: string; date: string; imageId: string }) {
  const res = useResource(`${scenarioId}/${date}/${imageId}`, () => getDecisionEvidence(scenarioId, date, imageId), FAIL)
  if (res.loading) return <p className="text-xs text-slate-400">읽는 중…</p>
  if (res.error !== null) return <p className="text-xs text-red-300">{res.error}</p>
  if (res.data === undefined) return null
  if (res.data === null) return <p className="text-xs text-slate-400">판정 기록을 찾지 못했습니다</p>
  return <EvidenceView evidence={res.data} />
}

function EvidenceView({ evidence: e }: { evidence: DecisionEvidence }) {
  const annotations = notableAnnotations(e.annotations)
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
    </div>
  )
}
