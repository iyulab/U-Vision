import type { CaptureMode } from '../lib/captureMode'
import { isInspectionFree } from '../lib/capturePolicy'
import type { MotionConfig } from '../lib/motion'
import type { Roi } from '../lib/roi'
import { useCamera } from '../hooks/useCamera'
import { useContinuousCapture } from '../hooks/useContinuousCapture'
import { useInspection } from '../hooks/useInspection'
import { useMotionDetection } from '../hooks/useMotionDetection'
import { useWakeLock } from '../hooks/useWakeLock'
import { InspectionHistory } from './InspectionHistory'
import { RoiOverlay } from './RoiOverlay'
import { VerdictBadge } from './VerdictBadge'

interface CameraViewProps {
  scenarioId: string
  roi: Roi
  motionConfig: MotionConfig
  minSharpness: number
  /** 촬영 모드 — auto: 정지감지 연속, manual: 셔터 버튼. */
  captureMode: CaptureMode
}

/**
 * 운영 화면(작업자) — 카메라 → (자동: 정지감지 연속 / 수동: 셔터) → 캡처 → 업로드 → OK/NG.
 * 자동 모드의 연속 발화는 useContinuousCapture 가 무손실(pull-on-free)로 관리한다.
 */
export function CameraView({ scenarioId, roi, motionConfig, minSharpness, captureMode }: CameraViewProps) {
  const { videoRef, ready, error } = useCamera()
  const inspection = useInspection(videoRef, scenarioId, roi, minSharpness)
  const auto = captureMode === 'auto'
  // 자동 모드만 모션 감지 worker 가동. 수동은 셔터로 trigger.
  const motion = useMotionDetection(videoRef, motionConfig, ready && auto)
  useContinuousCapture(motion, inspection.phase, inspection.trigger, ready && auto)
  useWakeLock(ready)

  const busy = !isInspectionFree(inspection.phase)
  const statusLabel = error
    ? `카메라 오류: ${error}`
    : !ready
      ? '카메라 준비 중…'
      : busy
        ? '검사 중…'
        : auto
          ? motion?.isStill
            ? '정지 — 검사'
            : '움직임 감지 중'
          : '수동 — 셔터를 누르세요'

  // 자동 모드: 판정 완료 + 품목 제거(모션 재개) → 다음 품목 안내.
  const showNextPrompt = auto && inspection.phase === 'done' && !(motion?.isStill ?? false)

  return (
    <div className="relative h-screen w-full overflow-hidden bg-black">
      <video ref={videoRef} className="h-full w-full object-cover" playsInline muted autoPlay />
      <RoiOverlay roi={roi} />

      <div className="absolute left-4 top-4 flex items-center gap-2 rounded-full bg-black/60 px-4 py-2 text-sm font-medium text-white backdrop-blur">
        <span
          className={`h-2.5 w-2.5 rounded-full ${
            error
              ? 'bg-red-500'
              : busy
                ? 'bg-sky-400 animate-pulse'
                : auto && motion?.isStill
                  ? 'bg-emerald-400'
                  : 'bg-amber-400 animate-pulse'
          }`}
        />
        {statusLabel}
      </div>

      <VerdictBadge result={inspection.latest} phase={inspection.phase} />
      <InspectionHistory items={inspection.history} />

      {/* 다음 품목 안내(자동 연속 플로우) */}
      {showNextPrompt && (
        <div className="absolute inset-x-0 bottom-24 flex justify-center">
          <div className="rounded-full bg-emerald-600/90 px-6 py-3 text-base font-semibold text-white">
            다음 품목을 올려놓으세요
          </div>
        </div>
      )}

      {/* 수동 셔터 */}
      {!auto && (
        <div className="absolute inset-x-0 bottom-8 flex justify-center">
          <button
            onClick={inspection.trigger}
            disabled={!ready || busy}
            className="h-20 w-20 rounded-full border-4 border-white bg-white/90 shadow-xl disabled:opacity-40"
            aria-label="촬영"
          />
        </div>
      )}

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
