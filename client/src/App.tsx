import { useCallback, useEffect, useMemo, useState } from 'react'

import { CameraView } from './components/CameraView'
import { Console } from './components/Console'
import { ResultsBrowser } from './components/ResultsBrowser'
import {
  clearStoredScenarioId,
  getStoredScenarioId,
  resolveActiveScenario,
  setStoredScenarioId,
} from './lib/activeScenario'
import { listScenarios } from './lib/api'
import { getCaptureMode, setCaptureMode, type CaptureMode } from './lib/captureMode'
import { getDeviceLabel, setDeviceLabel } from './lib/deviceIdentity'
import { DEFAULT_MOTION_CONFIG, type MotionConfig } from './lib/motion'
import { fromScenarioRoi } from './lib/roi'
import type { Scenario } from './lib/types'

/** 시나리오 캡처 설정 → 모션 감지 config. downscaleWidth 는 시나리오에 없으므로 클라 기본 보존. */
function toMotionConfig(s: Scenario): MotionConfig {
  return {
    motionThreshold: s.motion_threshold,
    stillFrames: s.still_frames,
    downscaleWidth: DEFAULT_MOTION_CONFIG.downscaleWidth,
  }
}

export default function App() {
  const [scenarios, setScenarios] = useState<Scenario[]>([])
  const [activeId, setActiveId] = useState<string | null>(() => getStoredScenarioId())
  const [showAdmin, setShowAdmin] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [captureMode, setCaptureModeState] = useState<CaptureMode>(() => getCaptureMode())
  const [deviceLabel, setDeviceLabelState] = useState<string>(() => getDeviceLabel())
  const [showTodayList, setShowTodayList] = useState(false)
  const todayUtc = new Date().toISOString().slice(0, 10) // 서버 date 버킷(UTC)과 정합

  function toggleCaptureMode() {
    const next: CaptureMode = captureMode === 'auto' ? 'manual' : 'auto'
    setCaptureModeState(next)
    setCaptureMode(next)
  }

  function updateDeviceLabel(label: string) {
    setDeviceLabelState(label)
    setDeviceLabel(label)
  }

  const reload = useCallback(async () => {
    try {
      setScenarios(await listScenarios())
    } catch {
      // 목록 로드 실패해도 화면은 뜬다(서버 미가동 등) — 안내는 zero-scenario 경로가 처리.
    } finally {
      setLoaded(true)
    }
  }, [])

  useEffect(() => {
    void reload()
  }, [reload])

  // 활성 시나리오 해석: 저장 id 를 live 목록과 대조해 동기화(삭제/최초실행/전체삭제 흡수).
  useEffect(() => {
    if (!loaded) return
    if (scenarios.length === 0) {
      if (activeId !== null) {
        setActiveId(null)
        clearStoredScenarioId()
      }
      return
    }
    const resolved = resolveActiveScenario(scenarios, activeId)!
    if (resolved.scenario_id !== activeId) {
      setActiveId(resolved.scenario_id)
      setStoredScenarioId(resolved.scenario_id)
    }
  }, [loaded, scenarios, activeId])

  const active = loaded ? resolveActiveScenario(scenarios, activeId) : null
  // worker 재생성을 막기 위해 config 객체를 active 기준으로 안정화.
  // ⚠️ 모든 Hook 은 조건부 return 위에 둔다 — admin 전환 시 hook 수가 달라지면 안 된다(Rules of Hooks).
  const motionConfig = useMemo(() => (active ? toMotionConfig(active) : null), [active])

  function selectScenario(id: string) {
    setActiveId(id)
    setStoredScenarioId(id)
  }

  if (showAdmin) {
    return (
      <Console
        onClose={() => setShowAdmin(false)}
        onChanged={reload}
        activeScenarioId={activeId}
      />
    )
  }

  return (
    <div className="relative h-screen w-full overflow-hidden bg-black">
      {active && motionConfig ? (
        <CameraView
          scenarioId={active.scenario_id}
          roi={fromScenarioRoi(active.roi)}
          motionConfig={motionConfig}
          minSharpness={active.min_sharpness}
          captureMode={captureMode}
        />
      ) : (
        <NoScenario loaded={loaded} onAdmin={() => setShowAdmin(true)} />
      )}

      {/* 우상단 컨트롤: 촬영 모드 + 태블릿 라벨 + 활성 시나리오 선택 + 관리 진입 */}
      <div className="absolute right-4 top-4 z-10 flex items-center gap-2">
        <button
          onClick={toggleCaptureMode}
          className="rounded-full bg-black/60 px-4 py-2 text-sm font-medium text-white backdrop-blur hover:bg-black/80"
        >
          {captureMode === 'auto' ? '자동' : '수동'}
        </button>
        <input
          value={deviceLabel}
          onChange={(e) => updateDeviceLabel(e.target.value)}
          placeholder="이 태블릿"
          className="w-28 rounded-full bg-black/60 px-3 py-2 text-sm text-white placeholder-white/50 backdrop-blur focus:outline-none"
        />
        {scenarios.length > 1 && active && (
          <select
            value={active.scenario_id}
            onChange={(e) => selectScenario(e.target.value)}
            className="rounded-full bg-black/60 px-3 py-2 text-sm font-medium text-white backdrop-blur focus:outline-none"
          >
            {scenarios.map((s) => (
              <option key={s.scenario_id} value={s.scenario_id} className="bg-slate-800">
                {s.name}
              </option>
            ))}
          </select>
        )}
        {active && (
          <button
            onClick={() => setShowTodayList(true)}
            className="rounded-full bg-black/60 px-4 py-2 text-sm font-medium text-white backdrop-blur hover:bg-black/80"
          >
            오늘 이력
          </button>
        )}
        <button
          onClick={() => setShowAdmin(true)}
          className="rounded-full bg-black/60 px-4 py-2 text-sm font-medium text-white backdrop-blur hover:bg-black/80"
        >
          관리
        </button>
      </div>

      {showTodayList && active && (
        <div className="fixed inset-0 z-30 flex flex-col bg-slate-900 text-slate-100">
          <header className="flex items-center justify-between border-b border-slate-700 px-6 py-4">
            <h1 className="text-lg font-semibold">
              오늘 이력 — {active.name} · {todayUtc}
            </h1>
            <button
              onClick={() => setShowTodayList(false)}
              className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium hover:bg-slate-600"
            >
              운영 화면
            </button>
          </header>
          <main className="flex-1 overflow-y-auto p-6">
            <ResultsBrowser
              scenarios={scenarios}
              initialScenarioId={active.scenario_id}
              lockedScenarioId={active.scenario_id}
              lockedDate={todayUtc}
            />
          </main>
        </div>
      )}
    </div>
  )
}

function NoScenario({ loaded, onAdmin }: { loaded: boolean; onAdmin: () => void }) {
  return (
    <div className="flex h-full w-full items-center justify-center p-6 text-center">
      <div className="space-y-4">
        <p className="text-lg text-slate-300">
          {loaded ? '등록된 검사 시나리오가 없습니다.' : '불러오는 중…'}
        </p>
        {loaded && (
          <button
            onClick={onAdmin}
            className="rounded-lg bg-emerald-500 px-5 py-2.5 text-sm font-semibold text-white hover:bg-emerald-400"
          >
            관리에서 시나리오 만들기
          </button>
        )}
      </div>
    </div>
  )
}
