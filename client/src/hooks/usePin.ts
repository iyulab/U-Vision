import { useCallback, useRef, useState } from 'react'

import { ApiError } from '../lib/api'

/** 변경 요청 1회의 결과 — 호출부가 busy/error/reload 처리를 분기한다. */
export type PinRunResult = 'ok' | 'cancelled' | 'unauthorized'

interface Resolver {
  resolve: (pin: string) => void
  reject: () => void
}

/**
 * 관리자 PIN 을 **lazy** 하게 수집·보관한다(섹션 콘솔 무인증 진입 모델).
 *
 * 읽기(목록·결과 조회)는 무인증이므로 진입 게이트를 두지 않는다 — PIN 은 변경(저장·삭제·업로드)
 * 시점에만 요구한다. 보관된 PIN 은 재사용하고, 401(불일치) 시 비워 다음 변경이 재프롬프트하게 한다.
 * 서버에 PIN-verify 엔드포인트가 없으므로 검증하지 않은 "인증됨" 상태를 만들지 않는다(lazy 검증).
 *
 * @returns runWithPin — 변경 액션을 PIN 과 함께 실행하는 중앙 러너.
 *          prompting/submitPin/cancelPin — 호출부가 PIN 모달을 렌더·제어한다.
 */
export function usePin() {
  const [pin, setPin] = useState<string | null>(null)
  const [prompting, setPrompting] = useState(false)
  const resolverRef = useRef<Resolver | null>(null)
  const pendingRef = useRef<Promise<string> | null>(null)

  const requirePin = useCallback((): Promise<string> => {
    if (pin !== null) return Promise.resolve(pin)
    // 이미 프롬프트 중이면 그 promise 를 재사용 — 이중 호출이 resolver 를 덮어 고착되는 것 방지.
    if (pendingRef.current) return pendingRef.current
    const p = new Promise<string>((resolve, reject) => {
      resolverRef.current = { resolve, reject: () => reject(new Error('cancelled')) }
    })
    pendingRef.current = p
    setPrompting(true)
    return p
  }, [pin])

  const submitPin = useCallback((value: string) => {
    setPin(value)
    setPrompting(false)
    resolverRef.current?.resolve(value)
    resolverRef.current = null
    pendingRef.current = null
  }, [])

  const cancelPin = useCallback(() => {
    setPrompting(false)
    resolverRef.current?.reject()
    resolverRef.current = null
    pendingRef.current = null
  }, [])

  const clearPin = useCallback(() => setPin(null), [])

  const runWithPin = useCallback(
    async (action: (pin: string) => Promise<void>): Promise<PinRunResult> => {
      let acquired: string
      try {
        acquired = await requirePin()
      } catch {
        return 'cancelled' // 사용자가 PIN 입력을 취소
      }
      try {
        await action(acquired)
        return 'ok'
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) {
          clearPin() // 보관 PIN 비움 → 다음 변경이 재프롬프트(resume)
          return 'unauthorized'
        }
        throw e // 그 외 오류는 호출부가 표면화
      }
    },
    [requirePin, clearPin],
  )

  return { runWithPin, prompting, submitPin, cancelPin }
}
