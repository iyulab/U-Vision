import { act, cleanup, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { ApiError } from '../lib/api'
import { usePin } from './usePin'

afterEach(cleanup)

describe('usePin', () => {
  it('처음 변경에서 PIN 을 묻고, 받은 PIN 을 다음 변경에 다시 쓴다', async () => {
    const { result } = renderHook(() => usePin())
    const action = vi.fn(async () => {})

    let first!: Promise<string>
    act(() => {
      first = result.current.runWithPin(action)
    })
    expect(result.current.prompting).toBe(true)
    await act(async () => result.current.submitPin('1234'))
    expect(await first).toBe('ok')
    expect(result.current.prompting).toBe(false)

    let second!: Promise<string>
    act(() => {
      second = result.current.runWithPin(action)
    })
    expect(result.current.prompting).toBe(false)
    expect(await second).toBe('ok')
    expect(action.mock.calls).toEqual([['1234'], ['1234']])
  })

  it('묻는 동안 들어온 두 번째 변경은 같은 입력을 기다린다', async () => {
    const { result } = renderHook(() => usePin())
    const action = vi.fn(async () => {})

    let a!: Promise<string>, b!: Promise<string>
    act(() => {
      a = result.current.runWithPin(action)
      b = result.current.runWithPin(action)
    })
    await act(async () => result.current.submitPin('9'))
    expect(await Promise.all([a, b])).toEqual(['ok', 'ok'])
    expect(action).toHaveBeenCalledTimes(2)
  })

  it('입력을 취소하면 cancelled 이고 변경은 실행되지 않는다', async () => {
    const { result } = renderHook(() => usePin())
    const action = vi.fn(async () => {})

    let run!: Promise<string>
    act(() => {
      run = result.current.runWithPin(action)
    })
    act(() => result.current.cancelPin())
    expect(await run).toBe('cancelled')
    expect(action).not.toHaveBeenCalled()
    expect(result.current.prompting).toBe(false)
  })

  it('401 이면 보관한 PIN 을 버리고 다음 변경이 다시 묻는다', async () => {
    const { result } = renderHook(() => usePin())

    let run!: Promise<string>
    act(() => {
      run = result.current.runWithPin(async () => {
        throw new ApiError(401, 'bad pin')
      })
    })
    await act(async () => result.current.submitPin('0000'))
    expect(await run).toBe('unauthorized')

    act(() => {
      void result.current.runWithPin(async () => {})
    })
    expect(result.current.prompting).toBe(true)
  })

  it('401 밖의 오류는 호출부로 던진다', async () => {
    const { result } = renderHook(() => usePin())

    let run!: Promise<string>
    act(() => {
      run = result.current.runWithPin(async () => {
        throw new ApiError(500, 'boom')
      })
      run.catch(() => {}) // 단언 전에 거부되므로 미처리 거부로 잡히지 않게
    })
    await act(async () => result.current.submitPin('1'))
    await expect(run).rejects.toThrow('boom')
  })
})
