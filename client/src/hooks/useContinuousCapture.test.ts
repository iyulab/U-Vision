import { cleanup, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { InspectionPhase } from '../lib/capturePolicy'
import type { StillnessState } from '../lib/motion'
import { useContinuousCapture } from './useContinuousCapture'

afterEach(cleanup)

const still: StillnessState = { isStill: true, stillStreak: 5 }
const moving: StillnessState = { isStill: false, stillStreak: 0 }

interface Props {
  motion: StillnessState | null
  phase: InspectionPhase
  enabled: boolean
}

function render(initial: Props) {
  const trigger = vi.fn()
  const hook = renderHook((p: Props) => useContinuousCapture(p.motion, p.phase, trigger, p.enabled), {
    initialProps: initial,
  })
  return { ...hook, trigger }
}

describe('useContinuousCapture', () => {
  it('정지 에피소드마다 한 번만 찍는다', () => {
    const { rerender, trigger } = render({ motion: still, phase: 'idle', enabled: true })
    expect(trigger).toHaveBeenCalledTimes(1)

    // 같은 정지가 이어지고 판정이 끝나도 다시 찍지 않는다.
    rerender({ motion: { isStill: true, stillStreak: 9 }, phase: 'done', enabled: true })
    expect(trigger).toHaveBeenCalledTimes(1)

    // 움직였다가 다시 멈추면 다음 품목이다.
    rerender({ motion: moving, phase: 'done', enabled: true })
    rerender({ motion: still, phase: 'done', enabled: true })
    expect(trigger).toHaveBeenCalledTimes(2)
  })

  it('판정 중에 멈춘 품목은 버리지 않고 검사기가 비면 찍는다', () => {
    const { rerender, trigger } = render({ motion: still, phase: 'uploading', enabled: true })
    expect(trigger).not.toHaveBeenCalled()

    rerender({ motion: still, phase: 'done', enabled: true })
    expect(trigger).toHaveBeenCalledTimes(1)
  })

  it('오프라인으로 큐에 남긴 뒤에도 다음 품목을 받는다', () => {
    const { rerender, trigger } = render({ motion: still, phase: 'idle', enabled: true })
    rerender({ motion: still, phase: 'queued', enabled: true })
    rerender({ motion: moving, phase: 'queued', enabled: true })
    rerender({ motion: still, phase: 'queued', enabled: true })
    expect(trigger).toHaveBeenCalledTimes(2)
  })

  it('차단 게이트 · 꺼진 자동촬영에서는 찍지 않는다', () => {
    const { rerender, trigger } = render({ motion: still, phase: 'blocked', enabled: true })
    rerender({ motion: moving, phase: 'blocked', enabled: false })
    rerender({ motion: still, phase: 'idle', enabled: false })
    expect(trigger).not.toHaveBeenCalled()
  })
})
