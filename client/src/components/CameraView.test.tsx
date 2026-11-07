import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { InspectionState } from '../hooks/useInspection'
import { CameraView } from './CameraView'

// 화면의 배선만 본다 — 카메라 · 감지 · 판정 상태머신은 바꿔 끼운다.
const h = vi.hoisted(() => ({ inspection: null as unknown as InspectionState }))
vi.mock('../hooks/useCamera', () => ({ useCamera: () => ({ videoRef: { current: null }, ready: false, error: 'NotFoundError' }) }))
vi.mock('../hooks/useMotionDetection', () => ({ useMotionDetection: () => null }))
vi.mock('../hooks/useContinuousCapture', () => ({ useContinuousCapture: () => undefined }))
vi.mock('../hooks/useWakeLock', () => ({ useWakeLock: () => undefined }))
vi.mock('../hooks/useInspection', () => ({ useInspection: () => h.inspection }))

const state = (over: Partial<InspectionState> = {}): InspectionState => ({
  phase: 'idle',
  latest: null,
  history: [],
  error: null,
  unavailable: null,
  pending: 0,
  storageDurable: true,
  queueNotice: null,
  lateAlerts: [],
  dismissLateAlerts: vi.fn(),
  trigger: vi.fn(),
  uploadFile: vi.fn(),
  acknowledge: vi.fn(),
  ...over,
})

const props = {
  scenarioId: 's',
  roi: { x: 0, y: 0, width: 0, height: 0 },
  motionConfig: { motionThreshold: 8, stillFrames: 5, downscaleWidth: 64 },
  minSharpness: 0,
  captureMode: 'manual' as const,
}

// vitest 전역을 켜지 않아 RTL 의 자동 정리가 걸리지 않는다.
afterEach(cleanup)

describe('CameraView 사진 파일 판정', () => {
  it('카메라가 없어도 사진 파일을 골라 판정할 수 있다 — JPEG · PNG 만 받는다', () => {
    h.inspection = state()
    render(<CameraView {...props} />)

    const input = screen.getByTestId('photo-upload') as HTMLInputElement
    expect(input.accept).toBe('image/jpeg,image/png')
    expect(screen.getByText('사진 파일로 판정')).toBeTruthy()

    const file = new File([new Uint8Array([1, 2])], 'p.png', { type: 'image/png' })
    fireEvent.change(input, { target: { files: [file] } })
    expect(h.inspection.uploadFile).toHaveBeenCalledWith(file)
  })

  it('판정이 진행 중이면 파일 선택을 막는다', () => {
    h.inspection = state({ phase: 'uploading' })
    render(<CameraView {...props} />)

    expect((screen.getByTestId('photo-upload') as HTMLInputElement).disabled).toBe(true)
  })
})
