import { useEffect, useState } from 'react'

import { getEventsStatus } from '../lib/api'
import type { EventsStatus } from '../lib/types'

/**
 * 판정 사건 내보내기(웹훅) 상태 — 받는 곳마다 보내기를 기다리는 · 포기한 사건 수. 받는 곳이 설정되지 않았으면
 * 아무것도 그리지 않는다. 사건이 쌓이고 있다는 것을 서버 파일을 뒤지지 않고 알게 한다.
 */
export function EventsStatusPanel() {
  const [status, setStatus] = useState<EventsStatus | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    getEventsStatus()
      .then((s) => !cancelled && setStatus(s))
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : '조회 실패'))
    return () => {
      cancelled = true
    }
  }, [])

  if (error) return <p className="mb-4 text-sm text-red-400">사건 내보내기 상태: {error}</p>
  if (!status?.enabled) return null

  return (
    <section className="mb-6 rounded-xl border border-slate-700 p-4" aria-labelledby="events-status-heading">
      <h2 id="events-status-heading" className="mb-2 text-sm font-semibold text-slate-300">
        판정 사건 내보내기(웹훅)
      </h2>
      <table className="w-full text-sm">
        <thead className="text-left text-slate-400">
          <tr>
            <th className="py-1 font-medium">받는 곳</th>
            <th className="py-1 font-medium">대기</th>
            <th className="py-1 font-medium">포기</th>
            <th className="py-1 font-medium">가장 오래된 대기</th>
            <th className="py-1 font-medium">마지막 실패</th>
          </tr>
        </thead>
        <tbody>
          {status.webhooks.map((w) => (
            <tr key={w.name} className="border-t border-slate-800">
              <td className="py-1">{w.name}</td>
              <td className="py-1">{w.pending}</td>
              <td className={`py-1 ${w.dead > 0 ? 'font-semibold text-red-400' : ''}`}>{w.dead}</td>
              <td className="py-1">
                {w.oldest_pending_at ? new Date(w.oldest_pending_at).toLocaleString('ko-KR') : '—'}
              </td>
              <td className="py-1">{w.last_error ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
