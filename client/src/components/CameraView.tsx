import { DEFAULT_MOTION_CONFIG } from '../lib/motion'
import { DEFAULT_ROI } from '../lib/roi'
import { useCamera } from '../hooks/useCamera'
import { useInspection } from '../hooks/useInspection'
import { useMotionDetection } from '../hooks/useMotionDetection'
import { useWakeLock } from '../hooks/useWakeLock'
import { InspectionHistory } from './InspectionHistory'
import { RoiOverlay } from './RoiOverlay'
import { VerdictBadge } from './VerdictBadge'

// C2 하드코딩 시나리오. 시나리오 선택 UI 는 P2(관리자 셋업, ~C8).
const SCENARIO_ID = 'demo'

/**
 * 운영 화면(작업자) — 코어 루프 완성:
 * 카메라 → 정지감지 → 캡처 → 업로드 → OK/NG 판정 표시.
 */
export function CameraView() {
  const { videoRef, ready, error } = useCamera()
  const inspection = useInspection(videoRef, SCENARIO_ID)
  // 정지 확정 순간 검사 트리거.
  const motion = useMotionDetection(videoRef, DEFAULT_MOTION_CONFIG, ready, inspection.trigger)
  // 카메라 준비되면 화면 유지.
  useWakeLock(ready)

  const statusLabel = error
    ? `카메라 오류: ${error}`
    : !ready
      ? '카메라 준비 중…'
      : motion?.isStill
        ? '정지 — 검사'
        : '움직임 감지 중'

  return (
    <div className="relative h-screen w-full overflow-hidden bg-black">
      <video ref={videoRef} className="h-full w-full object-cover" playsInline muted autoPlay />
      <RoiOverlay roi={DEFAULT_ROI} />

      {/* 상태 인디케이터 */}
      <div className="absolute left-4 top-4 flex items-center gap-2 rounded-full bg-black/60 px-4 py-2 text-sm font-medium text-white backdrop-blur">
        <span
          className={`h-2.5 w-2.5 rounded-full ${
            error ? 'bg-red-500' : motion?.isStill ? 'bg-emerald-400' : 'bg-amber-400 animate-pulse'
          }`}
        />
        {statusLabel}
      </div>

      <VerdictBadge result={inspection.latest} phase={inspection.phase} />
      <InspectionHistory items={inspection.history} />

      {/* 판정 오류 토스트 */}
      {inspection.error && (
        <div className="absolute inset-x-0 top-16 flex justify-center">
          <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm text-white">
            {inspection.error}
          </div>
        </div>
      )}
    </div>
  )
}
