import {
  blankClass,
  classDraftsError,
  MAX_CLASS_DESCRIPTION,
  type ClassDraft,
} from '../lib/classification'

/**
 * 다중 분류(`classification`)의 클래스 편집기 — 클래스마다 id · 표시 이름 · 설명 · 확정 허용 · 꼭 잡을 값.
 * 클래스는 값 중립이다: OK 도 불량 유형도 요구하지 않고(품번 · 품종 · 판독처럼), 선악은 «꼭 잡을 값» 한 칸이 정한다.
 * 서버가 거부할 입력은 저장 전에 사유와 함께 보인다. 이미 저장된 클래스의 id 는 기준 이미지 폴더 · 기억의 이름이라 고치지 않는다.
 */
export function ClassesField({
  classes,
  onChange,
}: {
  classes: ClassDraft[]
  onChange: (classes: ClassDraft[]) => void
}) {
  const error = classDraftsError(classes)
  const set = (index: number, patch: Partial<ClassDraft>) =>
    onChange(classes.map((c, i) => (i === index ? { ...c, ...patch } : c)))
  const input =
    'w-full rounded-lg border border-slate-600 bg-slate-900 px-2.5 py-1.5 text-sm text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none'
  return (
    <div className="space-y-2 rounded-lg border border-slate-700 p-4" data-testid="classes-field">
      <h3 className="text-sm font-semibold text-slate-200">클래스 ({classes.length})</h3>
      <p className="text-xs text-slate-500">
        모델이 사진마다 이 중 하나를 고릅니다(둘 이상). 설명은 모델에게 «이 클래스가 무엇인가» 를 알려 주는 말입니다.
        «꼭 잡을 값» 으로 정한 클래스가 나오면 결과가 NG 로 보이고 NG 재현율이 그 값을 따릅니다 — 정하지 않으면 늘 OK 로 보입니다.
      </p>
      <ul className="space-y-2">
        {classes.map((c, i) => (
          <li key={i} className="space-y-1.5 rounded-lg bg-slate-900/60 p-2.5" data-testid="class-row">
            <div className="flex gap-2">
              <input
                value={c.id}
                onChange={(e) => set(i, { id: e.target.value })}
                readOnly={c.saved}
                aria-label={`클래스 ${i + 1} id`}
                placeholder="id (영문) 예: A100"
                title={c.saved ? '저장된 클래스의 id 는 기준 이미지 · 기억의 이름이라 바꿀 수 없습니다' : undefined}
                className={`${input} font-mono ${c.saved ? 'opacity-60' : ''}`}
              />
              <input
                value={c.label}
                onChange={(e) => set(i, { label: e.target.value })}
                aria-label={`클래스 ${i + 1} 표시 이름`}
                placeholder="표시 이름(선택)"
                className={input}
              />
              <button
                type="button"
                onClick={() => onChange(classes.filter((_, j) => j !== i))}
                aria-label={`클래스 ${i + 1} 지우기`}
                className="shrink-0 rounded-lg bg-slate-700 px-2.5 text-sm text-slate-300 hover:bg-red-600/80"
              >
                ✕
              </button>
            </div>
            <textarea
              value={c.description}
              onChange={(e) => set(i, { description: e.target.value })}
              aria-label={`클래스 ${i + 1} 설명`}
              placeholder={`설명(선택, ${MAX_CLASS_DESCRIPTION}자까지) 예: 금색 커넥터가 달린 모델`}
              rows={2}
              className={input}
            />
            <div className="flex flex-wrap gap-x-5 gap-y-1 text-xs text-slate-300">
              <label className="flex items-center gap-1.5">
                <input
                  type="checkbox"
                  checked={c.settleable}
                  disabled={c.mustCatch}
                  onChange={(e) => set(i, { settleable: e.target.checked })}
                  className="accent-emerald-400"
                />
                아래 단(전용 모델 · 기억)이 확정해도 됨
              </label>
              <label className="flex items-center gap-1.5">
                <input
                  type="checkbox"
                  checked={c.mustCatch}
                  disabled={c.settleable}
                  onChange={(e) => set(i, { mustCatch: e.target.checked })}
                  className="accent-amber-400"
                />
                꼭 잡을 값(놓치면 안 됨)
              </label>
            </div>
          </li>
        ))}
      </ul>
      <button
        type="button"
        onClick={() => onChange([...classes, blankClass()])}
        className="rounded-lg bg-slate-700 px-3 py-1.5 text-sm text-slate-200 hover:bg-slate-600"
      >
        + 클래스 추가
      </button>
      {classes.some((c) => c.saved) && (
        <p className="text-xs text-slate-500">
          이미 있던 클래스를 지우면 그 기준 이미지 · 기억은 남고 새 판정에서 그 클래스가 나오지 않습니다. 이미 쌓인 기록은 바뀌지 않습니다.
        </p>
      )}
      {error && (
        <p className="text-xs text-red-300" data-testid="classes-error">
          {error}
        </p>
      )}
    </div>
  )
}
