import { useEffect, useState } from 'react'

import {
  ApiError,
  createScenario,
  deleteScenario,
  listScenarios,
  updateScenario,
} from '../lib/api'
import { DEFAULT_MOTION_CONFIG } from '../lib/motion'
import { DEFAULT_ROI, fromScenarioRoi, toScenarioRoi, type Roi } from '../lib/roi'
import type { Scenario } from '../lib/types'
import { ReferenceGallery } from './ReferenceGallery'
import { RoiEditor } from './RoiEditor'

const DEFAULT_MIN_SHARPNESS = 100

interface AdminViewProps {
  /** 운영 화면으로 복귀. */
  onClose: () => void
  /** 시나리오 목록이 바뀌면 상위(App)가 다시 로드하도록 알린다. */
  onChanged: () => void
}

interface DraftScenario {
  id?: string // 있으면 수정, 없으면 생성
  name: string
  criteria: string
  roi: Roi
  motionThreshold: number
  stillFrames: number
  minSharpness: number
}

/**
 * 관리자 셋업 뷰 — PIN 게이트 + 시나리오 CRUD.
 *
 * PIN 검증은 lazy 다(서버에 PIN-verify 엔드포인트 없음, 목록 GET 은 무인증). 게이트는 PIN 을
 * 수집·보관만 하고, 변경 요청이 401 이면 보관 PIN 을 비우고 재입력을 요구한다 — 검증하지 않은
 * "인증됨" 상태를 만들지 않는다.
 */
export function AdminView({ onClose, onChanged }: AdminViewProps) {
  const [pin, setPin] = useState<string | null>(null)

  if (pin === null) {
    return <PinGate onSubmit={setPin} onCancel={onClose} />
  }

  return (
    <ScenarioManager
      pin={pin}
      onClose={onClose}
      onChanged={onChanged}
      onPinRejected={() => setPin(null)}
    />
  )
}

function PinGate({
  onSubmit,
  onCancel,
}: {
  onSubmit: (pin: string) => void
  onCancel: () => void
}) {
  const [value, setValue] = useState('')

  return (
    <div className="flex h-screen w-full items-center justify-center bg-slate-900 p-6">
      <form
        className="w-full max-w-xs space-y-4 rounded-2xl bg-slate-800 p-6 shadow-xl"
        onSubmit={(e) => {
          e.preventDefault()
          if (value) onSubmit(value)
        }}
      >
        <h1 className="text-lg font-semibold text-white">관리자 PIN</h1>
        <input
          type="password"
          inputMode="numeric"
          autoFocus
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder="PIN 입력"
          className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
        />
        <div className="flex gap-2">
          <button
            type="button"
            onClick={onCancel}
            className="flex-1 rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium text-slate-200 hover:bg-slate-600"
          >
            취소
          </button>
          <button
            type="submit"
            disabled={!value}
            className="flex-1 rounded-lg bg-emerald-500 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-400 disabled:opacity-40"
          >
            확인
          </button>
        </div>
      </form>
    </div>
  )
}

function ScenarioManager({
  pin,
  onClose,
  onChanged,
  onPinRejected,
}: {
  pin: string
  onClose: () => void
  onChanged: () => void
  onPinRejected: () => void
}) {
  const [scenarios, setScenarios] = useState<Scenario[]>([])
  const [draft, setDraft] = useState<DraftScenario | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function reload() {
    try {
      setScenarios(await listScenarios())
    } catch (e) {
      setError(e instanceof Error ? e.message : '목록 로드 실패')
    }
  }

  useEffect(() => {
    void reload()
  }, [])

  /** 변경 요청을 실행하고 401(PIN 불일치)을 게이트 복귀로 처리한다. */
  async function withPin(action: () => Promise<void>) {
    setBusy(true)
    setError(null)
    try {
      await action()
      await reload()
      onChanged()
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) {
        onPinRejected() // PIN clear → 게이트 재진입
        return
      }
      setError(e instanceof Error ? e.message : '요청 실패')
    } finally {
      setBusy(false)
    }
  }

  function save() {
    if (!draft || !draft.name.trim()) return
    const input = {
      name: draft.name.trim(),
      criteria: draft.criteria.trim(),
      roi: toScenarioRoi(draft.roi),
      motion_threshold: draft.motionThreshold,
      still_frames: draft.stillFrames,
      min_sharpness: draft.minSharpness,
    }
    void withPin(async () => {
      if (draft.id) await updateScenario(draft.id, input, pin)
      else await createScenario(input, pin)
      setDraft(null)
    })
  }

  function remove(s: Scenario) {
    if (!window.confirm(`"${s.name}" 시나리오와 모든 검사 기록을 삭제합니다. 계속할까요?`)) return
    void withPin(() => deleteScenario(s.scenario_id, pin))
  }

  return (
    <div className="min-h-screen w-full bg-slate-900 text-slate-100">
      <header className="flex items-center justify-between border-b border-slate-700 px-6 py-4">
        <h1 className="text-lg font-semibold">시나리오 관리</h1>
        <div className="flex gap-2">
          <button
            onClick={() =>
              setDraft({
                name: '',
                criteria: '',
                roi: DEFAULT_ROI,
                motionThreshold: DEFAULT_MOTION_CONFIG.motionThreshold,
                stillFrames: DEFAULT_MOTION_CONFIG.stillFrames,
                minSharpness: DEFAULT_MIN_SHARPNESS,
              })
            }
            className="rounded-lg bg-emerald-500 px-4 py-2 text-sm font-semibold hover:bg-emerald-400"
          >
            + 새 시나리오
          </button>
          <button
            onClick={onClose}
            className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium hover:bg-slate-600"
          >
            운영 화면
          </button>
        </div>
      </header>

      {error && (
        <div className="mx-6 mt-4 rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>
      )}

      <main className="space-y-3 p-6">
        {scenarios.length === 0 && !draft && (
          <p className="text-slate-400">시나리오가 없습니다. 새 시나리오를 만들어 검사를 시작하세요.</p>
        )}

        {scenarios.map((s) => (
          <div
            key={s.scenario_id}
            className="flex items-start justify-between rounded-xl bg-slate-800 p-4"
          >
            <div className="min-w-0">
              <p className="font-medium">{s.name}</p>
              <p className="text-xs text-slate-400">{s.scenario_id}</p>
              {s.criteria && (
                <p className="mt-1 line-clamp-2 text-sm text-slate-300">{s.criteria}</p>
              )}
            </div>
            <div className="ml-4 flex shrink-0 gap-2">
              <button
                onClick={() =>
                  setDraft({
                    id: s.scenario_id,
                    name: s.name,
                    criteria: s.criteria,
                    roi: fromScenarioRoi(s.roi),
                    motionThreshold: s.motion_threshold,
                    stillFrames: s.still_frames,
                    minSharpness: s.min_sharpness,
                  })
                }
                className="rounded-lg bg-slate-700 px-3 py-1.5 text-sm hover:bg-slate-600"
              >
                수정
              </button>
              <button
                onClick={() => remove(s)}
                disabled={busy}
                className="rounded-lg bg-red-600/80 px-3 py-1.5 text-sm hover:bg-red-500 disabled:opacity-40"
              >
                삭제
              </button>
            </div>
          </div>
        ))}
      </main>

      {draft && (
        <ScenarioForm
          draft={draft}
          busy={busy}
          pin={pin}
          onPinRejected={onPinRejected}
          onChange={setDraft}
          onSave={save}
          onCancel={() => setDraft(null)}
        />
      )}
    </div>
  )
}

function Slider({
  label,
  hint,
  min,
  max,
  value,
  onChange,
}: {
  label: string
  hint: string
  min: number
  max: number
  value: number
  onChange: (v: number) => void
}) {
  return (
    <label className="block space-y-1">
      <span className="flex items-center justify-between text-sm text-slate-300">
        <span>{label}</span>
        <span className="font-mono text-emerald-400">{value}</span>
      </span>
      <input
        type="range"
        min={min}
        max={max}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        className="w-full accent-emerald-400"
      />
      <span className="text-xs text-slate-500">{hint}</span>
    </label>
  )
}

function ScenarioForm({
  draft,
  busy,
  pin,
  onPinRejected,
  onChange,
  onSave,
  onCancel,
}: {
  draft: DraftScenario
  busy: boolean
  pin: string
  onPinRejected: () => void
  onChange: (d: DraftScenario) => void
  onSave: () => void
  onCancel: () => void
}) {
  return (
    <div className="fixed inset-0 flex items-center justify-center bg-black/60 p-6">
      <div className="max-h-[90vh] w-full max-w-lg space-y-4 overflow-y-auto rounded-2xl bg-slate-800 p-6 shadow-xl">
        <h2 className="text-lg font-semibold text-white">
          {draft.id ? '시나리오 수정' : '새 시나리오'}
        </h2>
        <label className="block space-y-1">
          <span className="text-sm text-slate-300">이름</span>
          <input
            value={draft.name}
            onChange={(e) => onChange({ ...draft, name: e.target.value })}
            placeholder="예: PCB 상면 검사"
            className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-sm text-slate-300">판정 기준(자연어)</span>
          <textarea
            value={draft.criteria}
            onChange={(e) => onChange({ ...draft, criteria: e.target.value })}
            rows={4}
            placeholder="예: 표면에 긁힘·이물·균열이 없어야 한다. 결함이 보이면 NG."
            className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
          />
        </label>

        <RoiEditor roi={draft.roi} onChange={(roi) => onChange({ ...draft, roi })} />

        <div className="space-y-3 rounded-lg border border-slate-700 p-4">
          <h3 className="text-sm font-semibold text-slate-200">캡처 설정</h3>
          <Slider
            label="모션 임계"
            hint="낮을수록 민감(작은 움직임도 감지)"
            min={1}
            max={50}
            value={draft.motionThreshold}
            onChange={(v) => onChange({ ...draft, motionThreshold: v })}
          />
          <Slider
            label="정지 프레임"
            hint="이만큼 연속 정지하면 캡처"
            min={1}
            max={30}
            value={draft.stillFrames}
            onChange={(v) => onChange({ ...draft, stillFrames: v })}
          />
          <Slider
            label="최소 선명도"
            hint="흐림 거부 기준 — 저장만(Phase 3 적용 예정)"
            min={0}
            max={300}
            value={draft.minSharpness}
            onChange={(v) => onChange({ ...draft, minSharpness: v })}
          />
        </div>

        {draft.id ? (
          <ReferenceGallery scenarioId={draft.id} pin={pin} onPinRejected={onPinRejected} />
        ) : (
          <p className="text-xs text-slate-500">기준 이미지는 저장 후 추가할 수 있습니다.</p>
        )}

        <div className="flex justify-end gap-2">
          <button
            onClick={onCancel}
            className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium text-slate-200 hover:bg-slate-600"
          >
            취소
          </button>
          <button
            onClick={onSave}
            disabled={busy || !draft.name.trim()}
            className="rounded-lg bg-emerald-500 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-400 disabled:opacity-40"
          >
            저장
          </button>
        </div>
      </div>
    </div>
  )
}
