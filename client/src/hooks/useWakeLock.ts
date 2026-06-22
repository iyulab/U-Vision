import { useEffect } from 'react'

/**
 * 화면 꺼짐 방지(Wake Lock). 운영 화면이 활성인 동안 화면을 유지한다.
 *
 * iOS Safari 는 백그라운드 전환 시 lock 을 자동 해제하므로, visibilitychange 로
 * 포그라운드 복귀 때 재요청한다. 미지원 브라우저에서는 조용히 무시(graceful).
 */
export function useWakeLock(active: boolean): void {
  useEffect(() => {
    if (!active || !('wakeLock' in navigator)) return

    let sentinel: WakeLockSentinel | null = null
    let cancelled = false

    async function request() {
      try {
        sentinel = await navigator.wakeLock.request('screen')
      } catch {
        // 권한 거부/미지원 — 무시
      }
    }

    function onVisibility() {
      if (!cancelled && document.visibilityState === 'visible') void request()
    }

    void request()
    document.addEventListener('visibilitychange', onVisibility)

    return () => {
      cancelled = true
      document.removeEventListener('visibilitychange', onVisibility)
      void sentinel?.release().catch(() => {})
    }
  }, [active])
}
