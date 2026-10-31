import { useCallback, useEffect, useState } from 'react'
import '@uplatform/brand'

import { createScenario, deleteScenario, getAdminStatus, listScenarios, updateScenario } from '../lib/api'
import { derivedScenarioId, isValidScenarioId, needsExplicitScenarioId } from '../lib/scenarioId'
import { usePin, type PinRunResult } from '../hooks/usePin'
import { useResource } from '../hooks/useResource'
import { AdminSetup } from './AdminSetup'
import { classesFor, defectsOf, formatDefects, parseDefects, type Defect } from '../lib/defectClasses'
import { DEFAULT_MOTION_CONFIG } from '../lib/motion'
import { DEFAULT_ROI, fromScenarioRoi, toScenarioRoi, type Roi } from '../lib/roi'
import type { Scenario } from '../lib/types'
import { AuthorityPanel } from './AuthorityPanel'
import { LabelAudit } from './LabelAudit'
import { DataVolumePanel } from './DataVolumePanel'
import { EventsStatusPanel } from './EventsStatusPanel'
import { MetricsDashboard } from './MetricsDashboard'
import { ReferenceGallery } from './ReferenceGallery'
import { RetentionPanel } from './RetentionPanel'
import { ResultsBrowser } from './ResultsBrowser'
import { RoiEditor } from './RoiEditor'

const DEFAULT_MIN_SHARPNESS = 100
const DEFAULT_MAX_IMAGE_DIMENSION = 0 // 0 = 축소 없음(원본) — 서버 기본과 정합
const DEFAULT_REFERENCE_CAP = 4 // 라벨당 — 서버 기본과 정합

/** 해상도 레버 이산 프리셋(longest-side px). 0 = 원본. 측정 비단조 → 의미있는 지점만 노출. */
const RESOLUTION_PRESETS: { value: number; label: string }[] = [
  { value: 0, label: '원본' },
  { value: 768, label: '768' },
  { value: 512, label: '512' },
  { value: 384, label: '384' },
  { value: 256, label: '256' },
  { value: 128, label: '128' },
]

type Tab = 'scenarios' | 'results' | 'metrics' | 'audit' | 'authority' | 'retention'

interface ConsoleProps {
  /** 운영 화면으로 복귀. */
  onClose: () => void
  /** 시나리오 목록이 바뀌면 상위(App)가 다시 로드하도록 알린다. */
  onChanged: () => void
  /** 결과 조회 탭의 기본 시나리오(운영 활성 시나리오). */
  activeScenarioId: string | null
}

interface DraftScenario {
  id?: string // 있으면 수정, 없으면 생성
  /** 생성할 때 정하는 id(선택) — 비우면 서버가 이름에서 만든다. */
  newId?: string
  name: string
  criteria: string
  roi: Roi
  motionThreshold: number
  stillFrames: number
  minSharpness: number
  maxImageDimension: number
  referenceCap: number
  /** 시나리오 유형 — 검사(OK/NG) 또는 오투입 확인(기대 품번과 일치/불일치). */
  type: ScenarioType
  /** 수정 시작 때의 유형 — 바뀌었을 때만 type 을 보낸다(생략하면 서버가 저장된 값을 유지). */
  originalType?: ScenarioType
  /** 검사의 불량 유형 입력란(쉼표 구분) — 비우면 OK/NG. */
  defects: string
  /** 수정 시작 때의 불량 유형 — 바뀌었을 때만 classes 를 보낸다. */
  originalDefects: Defect[]
}

/** 시나리오 유형 — 이 화면이 고를 수 있는 것은 'inspection' · 'verification' 이고, 모르는 유형(서버가 더 지은 것)은 그대로 둔다. */
type ScenarioType = string

/**
 * 관리 콘솔 — **무인증 진입** + 탭(시나리오 / 결과 조회).
 *
 * 읽기(시나리오 목록·결과 조회)는 무인증이므로 진입 게이트가 없다. PIN 은 변경(저장·삭제·업로드)
 * 시점에 lazy 수집한다({@link usePin}) — 콘솔이 PIN 모달을 단일 소유하고, 모든 변경이 같은
 * runWithPin 메커니즘을 거친다(시나리오·기준이미지 공통).
 */
export function Console({ onClose, onChanged, activeScenarioId }: ConsoleProps) {
  const [tab, setTab] = useState<Tab>('scenarios')
  const { data, error, reload: reloadList } = useResource('scenarios', listScenarios, '목록 로드 실패')
  const scenarios = data ?? []
  // 최초 설정 필요 여부(null = 확인 중). setup_required 면 관리 콘솔 대신 부트스트랩 화면을 띄운다.
  const [setupRequired, setSetupRequired] = useState<boolean | null>(null)
  const { runWithPin, prompting, submitPin, cancelPin } = usePin()

  // 변경 뒤 다시 읽기 — 운영 화면의 시나리오 목록도 함께 새로 읽게 한다.
  const reload = useCallback(async () => {
    await reloadList()
    onChanged()
  }, [reloadList, onChanged])

  // PIN 미설정 상태를 감지해 최초 설정 화면 노출 여부를 결정한다(부트스트랩). 조회 실패 시엔
  // 게이트하지 않는다(콘솔 정상 진입 — 변경 시점의 PIN 프롬프트/503 이 fallback).
  useEffect(() => {
    getAdminStatus()
      .then((s) => setSetupRequired(s.setup_required))
      .catch(() => setSetupRequired(false))
  }, [])

  if (setupRequired === null) {
    return (
      <div className="flex min-h-screen w-full items-center justify-center bg-slate-900 text-slate-400">
        불러오는 중…
      </div>
    )
  }

  if (setupRequired) {
    return (
      <AdminSetup
        onDone={() => {
          setSetupRequired(false)
          void reload()
        }}
      />
    )
  }

  return (
    <div className="flex min-h-screen w-full flex-col bg-slate-900 text-slate-100">
      <header className="flex items-center justify-between gap-3 border-b border-slate-700 px-6 py-4">
        {/* 좁은 화면에서는 탭 줄이 가로로 넘친다(글자 단위 줄바꿈 대신) — 탭 이름은 한 줄을 지킨다. */}
        <div className="flex min-w-0 items-center gap-1 overflow-x-auto">
          <TabButton active={tab === 'scenarios'} onClick={() => setTab('scenarios')}>
            시나리오
          </TabButton>
          <TabButton active={tab === 'results'} onClick={() => setTab('results')}>
            결과 조회
          </TabButton>
          <TabButton active={tab === 'metrics'} onClick={() => setTab('metrics')}>
            메트릭
          </TabButton>
          <TabButton active={tab === 'audit'} onClick={() => setTab('audit')}>
            라벨 감사
          </TabButton>
          <TabButton active={tab === 'authority'} onClick={() => setTab('authority')}>
            권한 단계
          </TabButton>
          <TabButton active={tab === 'retention'} onClick={() => setTab('retention')}>
            보존
          </TabButton>
        </div>
        <button
          onClick={onClose}
          className="shrink-0 whitespace-nowrap rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium hover:bg-slate-600"
        >
          운영 화면
        </button>
      </header>

      {error && <div className="mx-6 mt-4 rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      <main className="flex-1 p-6">
        {tab === 'scenarios' ? (
          <ScenarioManager scenarios={scenarios} reload={reload} runWithPin={runWithPin} />
        ) : tab === 'results' ? (
          <ResultsBrowser scenarios={scenarios} initialScenarioId={activeScenarioId} />
        ) : tab === 'metrics' ? (
          <>
            <DataVolumePanel />
            <EventsStatusPanel />
            <MetricsDashboard scenarios={scenarios} initialScenarioId={activeScenarioId} />
          </>
        ) : tab === 'audit' ? (
          <LabelAudit scenarios={scenarios} initialScenarioId={activeScenarioId} />
        ) : tab === 'authority' ? (
          <AuthorityPanel
            scenarios={scenarios}
            initialScenarioId={activeScenarioId}
            runWithPin={runWithPin}
          />
        ) : (
          <RetentionPanel scenarios={scenarios} initialScenarioId={activeScenarioId} runWithPin={runWithPin} />
        )}
      </main>

      {/* 우산 브랜드 소속 표시 — 횡단 제품이 패밀리와 닿는 유일한 자리. 네트워크·쿠키 없음(목록은 패키지에 내장). */}
      <footer className="flex justify-end border-t border-slate-700 px-6 py-3">
        <uplatform-affiliation product="u-vision" lang="ko" theme="dark" />
      </footer>

      {prompting && <PinPrompt onSubmit={submitPin} onCancel={cancelPin} />}
    </div>
  )
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      onClick={onClick}
      className={`shrink-0 whitespace-nowrap rounded-lg px-4 py-2 text-sm font-semibold transition ${
        active ? 'bg-slate-700 text-white' : 'text-slate-400 hover:text-slate-200'
      }`}
    >
      {children}
    </button>
  )
}

/** 변경 시점에 뜨는 PIN 모달 — 콘솔 콘텐츠(시나리오 폼 등) 위에 z-50 으로 올라온다. */
function PinPrompt({
  onSubmit,
  onCancel,
}: {
  onSubmit: (pin: string) => void
  onCancel: () => void
}) {
  const [value, setValue] = useState('')

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-6">
      <form
        className="w-full max-w-xs space-y-4 rounded-2xl bg-slate-800 p-6 shadow-xl"
        onSubmit={(e) => {
          e.preventDefault()
          if (value) onSubmit(value)
        }}
      >
        <h1 className="text-lg font-semibold text-white">관리자 PIN</h1>
        <p className="text-xs text-slate-400">변경을 적용하려면 PIN 을 입력하세요.</p>
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
  scenarios,
  reload,
  runWithPin,
}: {
  scenarios: Scenario[]
  reload: () => Promise<void>
  runWithPin: (action: (pin: string) => Promise<void>) => Promise<PinRunResult>
}) {
  const [draft, setDraft] = useState<DraftScenario | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  /** 변경 액션을 PIN 과 함께 실행하고 결과(성공/취소/PIN불일치)에 따라 분기한다. */
  async function mutate(action: (pin: string) => Promise<void>, onOk?: () => void) {
    setBusy(true)
    setError(null)
    try {
      const result = await runWithPin(action)
      if (result === 'ok') {
        onOk?.()
        await reload()
      } else if (result === 'unauthorized') {
        setError('PIN 이 올바르지 않습니다. 다시 시도하세요.')
      }
      // 'cancelled' → 조용히 무시
    } catch (e) {
      setError(e instanceof Error ? e.message : '요청 실패')
    } finally {
      setBusy(false)
    }
  }

  function save() {
    if (!draft || !draft.name.trim()) return
    const parsed = parseDefects(draft.defects)
    if ('error' in parsed) return
    const newId = draft.id ? '' : (draft.newId ?? '').trim()
    if (newId && !isValidScenarioId(newId)) return
    // 불량 유형은 검사에서 바뀌었을 때만 보낸다 — 생략하면 서버가 저장된 classes 를 그대로 둔다.
    const defectsChanged =
      draft.type === 'inspection' && formatDefects(parsed.defects) !== formatDefects(draft.originalDefects)
    const input = {
      ...(newId ? { scenario_id: newId } : {}),
      name: draft.name.trim(),
      criteria: draft.criteria.trim(),
      roi: toScenarioRoi(draft.roi),
      motion_threshold: draft.motionThreshold,
      still_frames: draft.stillFrames,
      min_sharpness: draft.minSharpness,
      max_image_dimension: draft.maxImageDimension,
      reference_cap: draft.referenceCap,
      // 검사(기본)는 type 을 쓰지 않아 기존 시나리오 파일이 그대로다. 오투입 확인이거나 유형을 바꿨을 때만 보낸다.
      ...(draft.type === 'verification' || draft.type !== (draft.originalType ?? 'inspection')
        ? { type: draft.type }
        : {}),
      ...(defectsChanged ? { classes: classesFor(parsed.defects) } : {}),
    }
    void mutate(
      async (pin) => {
        if (draft.id) await updateScenario(draft.id, input, pin)
        else await createScenario(input, pin)
      },
      () => setDraft(null),
    )
  }

  function remove(s: Scenario) {
    if (!window.confirm(`"${s.name}" 시나리오와 모든 검사 기록을 삭제합니다. 계속할까요?`)) return
    void mutate((pin) => deleteScenario(s.scenario_id, pin))
  }

  return (
    <div className="space-y-3">
      <div className="flex justify-end">
        <button
          onClick={() =>
            setDraft({
              name: '',
              criteria: '',
              roi: DEFAULT_ROI,
              motionThreshold: DEFAULT_MOTION_CONFIG.motionThreshold,
              stillFrames: DEFAULT_MOTION_CONFIG.stillFrames,
              minSharpness: DEFAULT_MIN_SHARPNESS,
              maxImageDimension: DEFAULT_MAX_IMAGE_DIMENSION,
              referenceCap: DEFAULT_REFERENCE_CAP,
              type: 'inspection',
              defects: '',
              originalDefects: [],
            })
          }
          className="rounded-lg bg-emerald-500 px-4 py-2 text-sm font-semibold hover:bg-emerald-400"
        >
          + 새 시나리오
        </button>
      </div>

      {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

      {scenarios.length === 0 && !draft && (
        <p className="text-slate-400">시나리오가 없습니다. 새 시나리오를 만들어 검사를 시작하세요.</p>
      )}

      {scenarios.map((s) => (
        <div key={s.scenario_id} className="flex items-start justify-between rounded-xl bg-slate-800 p-4">
          <div className="min-w-0">
            <p className="font-medium">{s.name}</p>
            <p className="text-xs text-slate-400">
              {s.scenario_id}
              {s.type === 'verification' && (
                <span className="ml-2 rounded bg-amber-500/20 px-1.5 py-0.5 text-amber-300">오투입 확인</span>
              )}
              {defectsOf(s).length > 0 && (
                <span className="ml-2 rounded bg-red-500/20 px-1.5 py-0.5 text-red-300">
                  불량 유형 {defectsOf(s).map((d) => d.label ?? d.id).join(' · ')}
                </span>
              )}
            </p>
            {s.criteria && <p className="mt-1 line-clamp-2 text-sm text-slate-300">{s.criteria}</p>}
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
                  maxImageDimension: s.max_image_dimension,
                  referenceCap: s.reference_cap,
                  type: s.type ?? 'inspection',
                  originalType: s.type ?? 'inspection',
                  defects: formatDefects(defectsOf(s)),
                  originalDefects: defectsOf(s),
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

      {draft && (
        <ScenarioForm
          draft={draft}
          busy={busy}
          runWithPin={runWithPin}
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

function PresetSelect({
  label,
  hint,
  options,
  value,
  onChange,
}: {
  label: string
  hint: string
  options: { value: number; label: string }[]
  value: number
  onChange: (v: number) => void
}) {
  return (
    <label className="block space-y-1">
      <span className="text-sm text-slate-300">{label}</span>
      <div className="flex flex-wrap gap-1.5">
        {options.map((o) => (
          <button
            key={o.value}
            type="button"
            onClick={() => onChange(o.value)}
            className={`rounded-lg px-3 py-1.5 text-sm font-medium transition ${
              value === o.value
                ? 'bg-emerald-500 text-white'
                : 'bg-slate-900 text-slate-300 hover:bg-slate-700'
            }`}
          >
            {o.label}
          </button>
        ))}
      </div>
      <span className="text-xs text-slate-500">{hint}</span>
    </label>
  )
}

/**
 * 검사의 불량 유형 — 비우면 OK/NG 판정, 적으면 NG 를 그 유형으로 나눠 판정한다(VLM 이 유형을 대고, 사람 라벨 · 학습
 * 데이터도 유형으로 남는다). 이미 쌓인 기록의 라벨은 바뀌지 않는다.
 */
function DefectsField({ draft, onChange }: { draft: DraftScenario; onChange: (d: DraftScenario) => void }) {
  const parsed = parseDefects(draft.defects)
  return (
    <label className="block space-y-1">
      <span className="text-sm text-slate-300">불량 유형(선택)</span>
      <input
        value={draft.defects}
        onChange={(e) => onChange({ ...draft, defects: e.target.value })}
        placeholder="비우면 OK / NG. 예: scratch=긁힘, dent=찍힘, stain=얼룩"
        className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 font-mono text-sm text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
      />
      {'error' in parsed ? (
        <span className="block text-xs text-red-300">{parsed.error}</span>
      ) : (
        <span className="block text-xs text-slate-500">
          «id=표시 이름» 을 쉼표로 나눠 적습니다 — id(영문)는 학습 데이터 · 판정 기록의 키, 표시 이름은 화면 · 내보내기에
          보이는 말입니다. 유형을 적으면 판정이 NG 대신 유형을 남기고, 라벨 버튼도 유형별로 바뀝니다. 운영 화면은 그대로
          OK / NG 로 보입니다.
        </span>
      )}
    </label>
  )
}

/** 새 시나리오의 id — 비우면 서버가 이름에서 만든다. 한글 이름은 영문이 남지 않아 직접 정하라고 권한다. */
function ScenarioIdField({ draft, onChange }: { draft: DraftScenario; onChange: (d: DraftScenario) => void }) {
  const value = draft.newId ?? ''
  const invalid = value.trim() !== '' && !isValidScenarioId(value.trim())
  const recommend = value.trim() === '' && needsExplicitScenarioId(draft.name)
  return (
    <label className="block space-y-1">
      <span className="text-sm text-slate-300">식별자 (선택)</span>
      <input
        value={value}
        onChange={(e) => onChange({ ...draft, newId: e.target.value })}
        placeholder={draft.name.trim() ? derivedScenarioId(draft.name) : '예: line3-misload'}
        className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 font-mono text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
      />
      <span className={`block text-xs ${invalid || recommend ? 'text-amber-300' : 'text-slate-500'}`}>
        {invalid
          ? '영문 · 숫자 · . _ - 만 쓸 수 있습니다.'
          : recommend
            ? '이름에 영문이 없어 식별자가 알아보기 어려운 값이 됩니다 — 영문으로 정해 주세요(예: line3-misload).'
            : '데이터 폴더 · 판정 사건 · CSV 에 남고, 만든 뒤 바꿀 수 없습니다. 비우면 이름에서 만듭니다.'}
      </span>
    </label>
  )
}

function ScenarioForm({
  draft,
  busy,
  runWithPin,
  onChange,
  onSave,
  onCancel,
}: {
  draft: DraftScenario
  busy: boolean
  runWithPin: (action: (pin: string) => Promise<void>) => Promise<PinRunResult>
  onChange: (d: DraftScenario) => void
  onSave: () => void
  onCancel: () => void
}) {
  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-6">
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
        {!draft.id && <ScenarioIdField draft={draft} onChange={onChange} />}
        <div className="space-y-1">
          <span className="text-sm text-slate-300">유형</span>
          <div className="flex gap-1.5">
            {(
              [
                ['inspection', '검사 (OK / NG)'],
                ['verification', '오투입 확인 (기대 품번과 일치 / 불일치)'],
              ] as const
            ).map(([value, label]) => (
              <button
                key={value}
                type="button"
                onClick={() => onChange({ ...draft, type: value })}
                className={`rounded-lg px-3 py-1.5 text-sm font-medium transition ${
                  draft.type === value ? 'bg-emerald-500 text-white' : 'bg-slate-900 text-slate-300 hover:bg-slate-700'
                }`}
              >
                {label}
              </button>
            ))}
          </div>
          {draft.type === 'verification' && (
            <span className="block text-xs text-slate-500">
              작업자가 운영 화면에서 기대 품번을 넣고 촬영한다. 바코드를 읽을 수 있는 기기면 읽은 코드와도 대조한다.
            </span>
          )}
        </div>
        {draft.type === 'inspection' && <DefectsField draft={draft} onChange={onChange} />}
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
            hint="이보다 흐린 캡처는 판정에 보내지 않고 다시 찍게 한다(0 = 끔)"
            min={0}
            max={300}
            value={draft.minSharpness}
            onChange={(v) => onChange({ ...draft, minSharpness: v })}
          />
        </div>

        <div className="space-y-3 rounded-lg border border-slate-700 p-4">
          <h3 className="text-sm font-semibold text-slate-200">성능 레버 (속도 ↔ 정확도)</h3>
          <p className="text-xs text-slate-500">
            해상도↓·기준 장수↓ = 빠름, 해상도↑·기준 장수↑ = 정확. 부품마다 균형점이 다르니 직접 쓸어보며 맞추세요.
          </p>
          <PresetSelect
            label="이미지 해상도"
            hint="VLM 전송 전 longest-side 축소(px). 낮을수록 빠르나 미세 결함을 놓칠 수 있음."
            options={RESOLUTION_PRESETS}
            value={draft.maxImageDimension}
            onChange={(v) => onChange({ ...draft, maxImageDimension: v })}
          />
          <Slider
            label="기준 이미지 장수"
            hint="라벨(OK/NG)당 few-shot 장수 — 0=zero-shot, 많을수록 정확↑ 느림↑"
            min={0}
            max={8}
            value={draft.referenceCap}
            onChange={(v) => onChange({ ...draft, referenceCap: v })}
          />
        </div>

        {draft.id ? (
          <ReferenceGallery
            scenarioId={draft.id}
            runWithPin={runWithPin}
            defectNames={draft.originalDefects.map((d) => d.label ?? d.id)}
            scenarioType={draft.originalType}
          />
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
            disabled={
              busy ||
              !draft.name.trim() ||
              'error' in parseDefects(draft.defects) ||
              (!draft.id && !!draft.newId?.trim() && !isValidScenarioId(draft.newId.trim()))
            }
            className="rounded-lg bg-emerald-500 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-400 disabled:opacity-40"
          >
            저장
          </button>
        </div>
      </div>
    </div>
  )
}
