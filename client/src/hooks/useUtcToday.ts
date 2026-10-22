import { useEffect, useState } from 'react'

/** 오늘 날짜 `YYYY-MM-DD`(UTC) — 서버의 날짜 폴더(수신 시각 UTC)와 같은 기준. */
export function utcToday(now: Date = new Date()): string {
  return now.toISOString().slice(0, 10)
}

/**
 * 오늘 날짜(UTC)를 상태로 들고, UTC 자정이 지나면 바꾼다 — 밤새 켜 두는 태블릿에서 «오늘 이력»이 어제에 머물지 않게.
 */
export function useUtcToday(): string {
  const [today, setToday] = useState(() => utcToday())

  useEffect(() => {
    const now = new Date()
    const nextMidnight = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1)
    const timer = setTimeout(() => setToday(utcToday()), nextMidnight - now.getTime() + 1000)
    return () => clearTimeout(timer)
  }, [today])

  return today
}
