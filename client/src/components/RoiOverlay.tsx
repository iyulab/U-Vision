import type { Roi } from '../lib/roi'

/** 카메라 위에 검사 구역(ROI)을 사각형으로 오버레이한다. 입력은 받지 않는다. */
export function RoiOverlay({ roi }: { roi: Roi }) {
  return (
    <div className="pointer-events-none absolute inset-0">
      <div
        className="absolute rounded-md border-2 border-emerald-400/90 shadow-[0_0_0_9999px_rgba(0,0,0,0.35)]"
        style={{
          left: `${roi.x * 100}%`,
          top: `${roi.y * 100}%`,
          width: `${roi.width * 100}%`,
          height: `${roi.height * 100}%`,
        }}
      />
    </div>
  )
}
