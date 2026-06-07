import { useCallback, useEffect, useMemo, useState } from 'react'

import { AdminView } from './components/AdminView'
import { CameraView } from './components/CameraView'
import {
  clearStoredScenarioId,
  getStoredScenarioId,
  resolveActiveScenario,
  setStoredScenarioId,
} from './lib/activeScenario'
import { listScenarios } from './lib/api'
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

  if (showAdmin) {
    return <AdminView onClose={() => setShowAdmin(false)} onChanged={reload} />
  }

  const active = loaded ? resolveActiveScenario(scenarios, activeId) : null
  // worker 재생성을 막기 위해 config 객체를 active 기준으로 안정화.
  const motionConfig = useMemo(() => (active ? toMotionConfig(active) : null), [active])

  function selectScenario(id: string) {
    setActiveId(id)
    setStoredScenarioId(id)
  }

  return (
    <div className="relative h-screen w-full overflow-hidden bg-black">
      {active && motionConfig ? (
        <CameraView
          scenarioId={active.scenario_id}
          roi={fromScenarioRoi(active.roi)}
          motionConfig={motionConfig}
          minSharpness={active.min_sharpness}
        />
      ) : (
        <NoScenario loaded={loaded} onAdmin={() => setShowAdmin(true)} />
      )}

      {/* 우상단 컨트롤: 활성 시나리오 선택 + 관리 진입 */}
      <div className="absolute right-4 top-4 z-10 flex items-center gap-2">
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
        <button
          onClick={() => setShowAdmin(true)}
          className="rounded-full bg-black/60 px-4 py-2 text-sm font-medium text-white backdrop-blur hover:bg-black/80"
        >
          관리
        </button>
      </div>
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
