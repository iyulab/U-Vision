import { useRef, type PointerEvent as ReactPointerEvent } from 'react'

import type { Roi } from '../lib/roi'

interface RoiEditorProps {
  roi: Roi
  onChange: (roi: Roi) => void
}

const MIN_SIZE = 0.05 // 너무 작은 ROI 방지(상대)

type DragMode = 'move' | 'resize'

/**
 * ROI 시각 편집기 — aspect-ratio 박스 위에서 검사 구역을 드래그로 이동/리사이즈한다.
 *
 * 라이브 카메라가 필요 없다(relative ROI 는 비례 사각형). WYSIWYG(실제 카메라 위 미리보기)은
 * 운영 화면 device 검증 영역 — 여기서는 좌표를 정확히 잡는 데 집중한다.
 */
export function RoiEditor({ roi, onChange }: RoiEditorProps) {
  const boxRef = useRef<HTMLDivElement>(null)
  const drag = useRef<{ mode: DragMode; startX: number; startY: number; start: Roi } | null>(null)

  function relative(e: ReactPointerEvent): { px: number; py: number } {
    const rect = boxRef.current!.getBoundingClientRect()
    return { px: (e.clientX - rect.left) / rect.width, py: (e.clientY - rect.top) / rect.height }
  }

  function begin(mode: DragMode, e: ReactPointerEvent) {
    e.stopPropagation()
    ;(e.currentTarget as Element).setPointerCapture(e.pointerId)
    const { px, py } = relative(e)
    drag.current = { mode, startX: px, startY: py, start: roi }
  }

  function move(e: ReactPointerEvent) {
    const d = drag.current
    if (!d) return
    const { px, py } = relative(e)
    const dx = px - d.startX
    const dy = py - d.startY

    if (d.mode === 'move') {
      const x = Math.min(Math.max(0, d.start.x + dx), 1 - d.start.width)
      const y = Math.min(Math.max(0, d.start.y + dy), 1 - d.start.height)
      onChange({ ...roi, x, y })
    } else {
      const width = Math.min(Math.max(MIN_SIZE, d.start.width + dx), 1 - d.start.x)
      const height = Math.min(Math.max(MIN_SIZE, d.start.height + dy), 1 - d.start.y)
      onChange({ ...roi, width, height })
    }
  }

  function end() {
    drag.current = null
  }

  return (
    <div className="space-y-2">
      <span className="text-sm text-slate-300">검사 구역(ROI)</span>
      <div
        ref={boxRef}
        className="relative aspect-video w-full overflow-hidden rounded-lg bg-slate-900 ring-1 ring-slate-700"
      >
        {/* 격자 가이드 */}
        <div className="pointer-events-none absolute inset-0 grid grid-cols-3 grid-rows-3">
          {Array.from({ length: 9 }).map((_, i) => (
            <div key={i} className="border border-slate-800" />
          ))}
        </div>

        {/* ROI 박스(이동) */}
        <div
          onPointerDown={(e) => begin('move', e)}
          onPointerMove={move}
          onPointerUp={end}
          className="absolute cursor-move rounded border-2 border-emerald-400 bg-emerald-400/10"
          style={{
            left: `${roi.x * 100}%`,
            top: `${roi.y * 100}%`,
            width: `${roi.width * 100}%`,
            height: `${roi.height * 100}%`,
            touchAction: 'none',
          }}
        >
          {/* 우하단 리사이즈 핸들 */}
          <div
            onPointerDown={(e) => begin('resize', e)}
            onPointerMove={move}
            onPointerUp={end}
            className="absolute -bottom-1.5 -right-1.5 h-4 w-4 cursor-se-resize rounded-full border-2 border-white bg-emerald-400"
            style={{ touchAction: 'none' }}
          />
        </div>
      </div>
      <p className="text-xs text-slate-500">
        박스를 끌어 위치를, 모서리 핸들로 크기를 조절합니다.
      </p>
    </div>
  )
}
