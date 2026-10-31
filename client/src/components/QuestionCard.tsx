import { useQuestionQueue } from '../hooks/useQuestionQueue'
import { questionImageUrl } from '../lib/api'
import { getDeviceId } from '../lib/deviceIdentity'
import { classNameOf } from '../lib/labels'
import { choiceTone } from '../lib/question'
import type { Scenario } from '../lib/types'

const TONE = {
  ok: 'bg-emerald-600 hover:bg-emerald-500',
  defect: 'bg-rose-600 hover:bg-rose-500',
} as const

/**
 * 한 번에 한 사진 — «이 사진은 무엇인가요?» 를 푼다. 엄지로 누르는 큰 버튼(클래스마다 하나 + 모르겠음)만 둔다. 어떤 이유로
 * 이 사진이 골라졌는지는 보이지 않는다(고른 방식이 답에 영향을 주지 않게). 카메라 위에 덮는 화면이라 카메라는 계속 살아 있다.
 */
export function QuestionCard({ scenario }: { scenario: Scenario }) {
  const queue = useQuestionQueue(scenario.scenario_id, getDeviceId())
  const nameOf = classNameOf(scenario)
  const { view } = queue

  return (
    <div className="mx-auto flex max-w-2xl flex-col gap-4">
      <p className="text-sm text-slate-400">
        이번에 푼 사진 <span className="font-semibold text-slate-200">{queue.solved}</span>장 · 확실하지 않으면 «모르겠음»
        을 누르세요.
      </p>

      {view.phase === 'loading' && <p className="py-16 text-center text-slate-400">불러오는 중…</p>}

      {view.phase === 'empty' && (
        <div className="space-y-3 py-16 text-center">
          <p className="text-lg text-slate-200">지금은 확인할 사진이 없습니다.</p>
          <p className="text-sm text-slate-400">검사가 쌓이면 다시 오세요.</p>
          <button
            onClick={queue.retry}
            className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium hover:bg-slate-600"
          >
            다시 확인
          </button>
        </div>
      )}

      {view.phase === 'error' && (
        <div className="space-y-3 py-16 text-center" role="alert">
          <p className="text-slate-200">{view.message}</p>
          <button
            onClick={queue.retry}
            className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium hover:bg-slate-600"
          >
            다시 시도
          </button>
        </div>
      )}

      {view.phase === 'ready' && (
        <>
          <img
            key={view.question.question_id}
            src={questionImageUrl(view.question.scenario_id, view.question.question_id)}
            alt="확인할 사진"
            className="max-h-[60vh] w-full rounded-lg bg-black object-contain"
          />
          <h2 className="text-center text-base font-semibold">이 사진은 무엇인가요?</h2>
          {queue.answerError && (
            <p className="text-center text-sm text-rose-300" role="alert">
              {queue.answerError}
            </p>
          )}
          <div className="grid grid-cols-2 gap-3">
            {view.question.choices.map((id) => (
              <button
                key={id}
                disabled={queue.busy}
                onClick={() => void queue.answer({ kind: 'class', id })}
                className={`min-h-16 rounded-xl px-4 text-lg font-semibold text-white disabled:opacity-50 ${TONE[choiceTone(id)]}`}
              >
                {nameOf(id)}
              </button>
            ))}
          </div>
          <button
            disabled={queue.busy}
            onClick={() => void queue.answer({ kind: 'abstain' })}
            className="min-h-14 rounded-xl bg-slate-700 px-4 text-base font-medium text-slate-100 hover:bg-slate-600 disabled:opacity-50"
          >
            모르겠음
          </button>
        </>
      )}
    </div>
  )
}
