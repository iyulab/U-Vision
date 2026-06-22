import { resultImageUrl } from '../lib/api'
import { LABEL_SET, agreementOf } from '../lib/labels'
import { reviewStateOf, type ReviewState } from '../lib/review'
import type { MlResult, StoredLabel, StoredResult, Verdict } from '../lib/types'
import { conflictBadgeLabel } from './conflictBadge'

/**
 * 결과 표 — 썸네일 + VLM 판정 + ML 교차검증(③) + 검토 상태(④-A) + 사람 라벨(인라인 OK/NOK).
 * 표현 전용: 데이터·콜백은 컨테이너(ResultsBrowser)가 소유한다.
 */
export function ResultsTable({
  results,
  labels,
  date,
  onLabel,
  onSelect,
}: {
  results: StoredResult[]
  labels: Map<string, StoredLabel>
  date: string
  /** label=null → 라벨 해제(삭제). 그 외 → 해당 라벨로 설정/정정. */
  onLabel: (imageId: string, label: string | null) => void
  /** 썸네일 클릭 → 확대 상세(48px 썸네일로는 미세 결함 판단 불가 — 확대 후 라벨). */
  onSelect: (result: StoredResult) => void
}) {
  // ③ ML 교차검증이 하나라도 있을 때만 ML/검토 컬럼을 띄운다(ML 비활성 = 기존 표와 동일).
  const hasMl = results.some((r) => r.ml != null)
  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-sm">
        <thead>
          <tr className="border-b border-slate-700 text-left text-xs text-slate-400">
            <th className="px-2 py-2">캡처</th>
            <th className="px-2 py-2">시각</th>
            <th className="px-2 py-2">AI 판정</th>
            <th className="px-2 py-2">신뢰도</th>
            {hasMl && <th className="px-2 py-2">ML 교차검증</th>}
            {hasMl && <th className="px-2 py-2">검토</th>}
            <th className="px-2 py-2">태블릿</th>
            <th className="px-2 py-2">사람 라벨</th>
            <th className="px-2 py-2">일치</th>
          </tr>
        </thead>
        <tbody>
          {results.map((r) => {
            const current = labels.get(r.image_id)?.label
            const review = reviewStateOf(r, labels.get(r.image_id))
            return (
              <tr
                key={r.image_id}
                className={`border-b border-slate-800 hover:bg-slate-800/50 ${
                  review === 'pending' ? 'bg-amber-500/5' : ''
                }`}
              >
                <td className="px-2 py-2">
                  <button onClick={() => onSelect(r)} title="확대">
                    <img
                      src={resultImageUrl(r.scenario_id, date, r.image_id)}
                      alt="검사 캡처"
                      loading="lazy"
                      className="h-12 w-12 rounded bg-black object-cover hover:ring-2 hover:ring-emerald-400"
                    />
                  </button>
                </td>
                <td className="px-2 py-2 text-slate-300">{formatTime(r.timestamp)}</td>
                <td className="px-2 py-2">
                  <VerdictPill verdict={r.verdict} />
                </td>
                <td className="px-2 py-2 font-mono text-slate-400">
                  {(r.confidence * 100).toFixed(0)}%
                </td>
                {hasMl && (
                  <td className="px-2 py-2">
                    <MlCheck ml={r.ml} verdict={r.verdict} agreement={r.agreement} />
                  </td>
                )}
                {hasMl && (
                  <td className="px-2 py-2">
                    <ReviewBadge state={review} />
                  </td>
                )}
                <td className="px-2 py-2 text-slate-400">{r.device_label || '—'}</td>
                <td className="px-2 py-2">
                  <div className="flex gap-1">
                    {LABEL_SET.map((value) => (
                      <button
                        key={value}
                        onClick={() => onLabel(r.image_id, current === value ? null : value)}
                        className={`rounded px-2 py-1 text-xs font-bold ${
                          current === value
                            ? value === 'NG'
                              ? 'bg-red-600 text-white'
                              : 'bg-emerald-600 text-white'
                            : 'bg-slate-700 text-slate-300 hover:bg-slate-600'
                        }`}
                      >
                        {value}
                      </button>
                    ))}
                  </div>
                  {conflictBadgeLabel(labels.get(r.image_id)) && (
                    <span className="ml-2 rounded-full bg-amber-500/20 px-2 py-0.5 text-xs font-bold text-amber-300">
                      {conflictBadgeLabel(labels.get(r.image_id))}
                    </span>
                  )}
                </td>
                <td className="px-2 py-2">
                  <AgreementMark verdict={r.verdict} label={current} />
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/**
 * ③ ML 교차검증 셀 — 전용 ML 라벨·신뢰도 + VLM 과의 일치(✓/✗).
 * ML 비활성/실패 레코드(ml 부재)는 "—".
 */
function MlCheck({
  ml,
  verdict,
  agreement,
}: {
  ml: MlResult | undefined
  verdict: Verdict
  agreement: boolean | undefined
}) {
  if (ml == null) return <span className="text-slate-600">—</span>
  // agreement 는 서버가 산출(③). 누락된 구 레코드는 라벨 비교로 보강(class-agnostic).
  const agree = agreement ?? ml.label.toUpperCase() === verdict
  return (
    <span className="inline-flex items-center gap-1 font-mono text-xs">
      <span className={agree ? 'text-slate-300' : 'text-amber-300'}>{ml.label.toUpperCase()}</span>
      <span className="text-slate-500">{(ml.confidence * 100).toFixed(0)}%</span>
      {agree ? (
        <span className="text-emerald-400" title="VLM 판정과 일치">
          ✓
        </span>
      ) : (
        <span className="text-amber-400" title="VLM 판정과 불일치(검토 가치)">
          ✗
        </span>
      )}
    </span>
  )
}

/** ④-A 검토 상태 배지 — 대기(amber)/검토됨(emerald)/자동확정(—). */
function ReviewBadge({ state }: { state: ReviewState }) {
  if (state === 'pending')
    return (
      <span className="inline-flex items-center rounded bg-amber-500/20 px-2 py-0.5 text-xs font-bold text-amber-300">
        검토 대기
      </span>
    )
  if (state === 'reviewed')
    return (
      <span className="inline-flex items-center text-xs text-emerald-400" title="사람 확인으로 해소">
        검토됨
      </span>
    )
  return <span className="text-slate-600">—</span>
}

function AgreementMark({ verdict, label }: { verdict: Verdict; label: string | undefined }) {
  const state = agreementOf(verdict, label)
  if (state === 'unlabeled') return <span className="text-slate-600">—</span>
  return state === 'match' ? (
    <span className="text-emerald-400" title="AI 판정과 일치">
      ✓
    </span>
  ) : (
    <span className="text-amber-400" title="AI 판정과 불일치(검토 가치)">
      ✗
    </span>
  )
}

function VerdictPill({ verdict }: { verdict: Verdict }) {
  const ng = verdict === 'NG'
  return (
    <span
      className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-bold text-white ${
        ng ? 'bg-red-600' : 'bg-emerald-600'
      }`}
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
