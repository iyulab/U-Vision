import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { useResource } from './useResource'

afterEach(cleanup)

function deferred<T>() {
  let resolve!: (v: T) => void
  let reject!: (e: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

describe('useResource', () => {
  it('키가 없으면 읽지 않는다', () => {
    const load = vi.fn(async () => 1)
    const { result } = renderHook(() => useResource(null, load, '실패'))
    expect(load).not.toHaveBeenCalled()
    expect(result.current).toMatchObject({ data: undefined, error: null, loading: false })
  })

  it('첫 응답 전에는 loading, 응답 뒤에는 값', async () => {
    const { result } = renderHook(() => useResource('a', async () => 42, '실패'))
    expect(result.current.loading).toBe(true)
    await waitFor(() => expect(result.current.data).toBe(42))
    expect(result.current.loading).toBe(false)
  })

  it('실패는 Error 메시지를, Error 가 아니면 failMessage 를 보인다', async () => {
    const { result, rerender } = renderHook(({ k }) => useResource(k, () => Promise.reject(k === 'a' ? new Error('없음') : 'x'), '로드 실패'), {
      initialProps: { k: 'a' },
    })
    await waitFor(() => expect(result.current.error).toBe('없음'))
    rerender({ k: 'b' })
    await waitFor(() => expect(result.current.error).toBe('로드 실패'))
    expect(result.current.data).toBeUndefined()
  })

  it('키가 바뀌면 이전 키의 값을 보이지 않고, 늦게 온 이전 응답은 버린다', async () => {
    const slowA = deferred<string>()
    const { result, rerender } = renderHook(({ k }) => useResource(k, () => (k === 'a' ? slowA.promise : Promise.resolve('B')), '실패'), {
      initialProps: { k: 'a' },
    })
    rerender({ k: 'b' })
    await waitFor(() => expect(result.current.data).toBe('B'))
    await act(async () => slowA.resolve('A'))
    expect(result.current.data).toBe('B')
  })

  it('reload 는 이전 값을 보인 채 다시 읽고, 끝나면 풀린다', async () => {
    let n = 0
    const { result } = renderHook(() => useResource('a', async () => ++n, '실패'))
    await waitFor(() => expect(result.current.data).toBe(1))

    let reloaded!: Promise<void>
    act(() => {
      reloaded = result.current.reload()
    })
    expect(result.current).toMatchObject({ data: 1, loading: false })
    await act(async () => reloaded)
    expect(result.current.data).toBe(2)
  })

  it('load 를 렌더마다 새로 만들어도 다시 읽지 않는다', async () => {
    const calls = vi.fn()
    const { result, rerender } = renderHook(() =>
      useResource('a', async () => {
        calls()
        return 1
      }, '실패'),
    )
    await waitFor(() => expect(result.current.data).toBe(1))
    rerender()
    rerender()
    expect(calls).toHaveBeenCalledTimes(1)
  })
})
