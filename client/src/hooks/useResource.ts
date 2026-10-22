import { useCallback, useEffect, useEffectEvent, useRef, useState } from 'react'

export interface Resource<T> {
  /** 지금 키로 받은 값 — 아직 없거나 실패했으면 `undefined`. */
  data: T | undefined
  error: string | null
  /** 지금 키의 첫 응답을 기다리는 중. 다시 읽기(`reload`) 동안은 이전 값을 그대로 보인다. */
  loading: boolean
  /** 같은 키로 다시 읽는다. 그 읽기가 끝나면(성공 · 실패 · 키 전환) 풀린다. */
  reload: () => Promise<void>
}

type Settled<T> = { key: string; ok: true; data: T } | { key: string; ok: false; error: string }

/**
 * 키가 정하는 서버 자원 하나를 읽는다. 결과를 «어느 키의 결과인가»와 함께 두고 지금 키와 맞을 때만 보이므로, 키가
 * 바뀔 때 이전 값 · 오류 · 로딩을 effect 에서 되돌릴 일이 없다 — 늦게 온 이전 키의 응답도 버려진다. `key` 가
 * `null` 이면 읽지 않는다(선택 전). `load` 는 렌더마다 새로 만들어도 된다: 다시 읽는 조건은 키와 `reload` 뿐이다.
 */
export function useResource<T>(key: string | null, load: () => Promise<T>, failMessage: string): Resource<T> {
  const [settled, setSettled] = useState<Settled<T> | null>(null)
  const [nonce, setNonce] = useState(0)
  const waiters = useRef<(() => void)[]>([])
  const run = useEffectEvent(load)

  useEffect(() => {
    if (key === null) return
    let cancelled = false
    const done = waiters.current.splice(0)
    run()
      .then(
        (data) => {
          if (!cancelled) setSettled({ key, ok: true, data })
        },
        (e: unknown) => {
          if (!cancelled) setSettled({ key, ok: false, error: e instanceof Error ? e.message : failMessage })
        },
      )
      .finally(() => done.forEach((resolve) => resolve()))
    return () => {
      cancelled = true
    }
  }, [key, nonce, failMessage])

  const reload = useCallback(() => {
    if (key === null) return Promise.resolve()
    return new Promise<void>((resolve) => {
      waiters.current.push(resolve)
      setNonce((n) => n + 1)
    })
  }, [key])

  const current = settled !== null && settled.key === key ? settled : null
  return {
    data: current?.ok ? current.data : undefined,
    error: current !== null && !current.ok ? current.error : null,
    loading: key !== null && current === null,
    reload,
  }
}
