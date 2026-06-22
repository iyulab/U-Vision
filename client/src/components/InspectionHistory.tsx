import type { InspectResult } from '../lib/types'

const RECENT_LIMIT = 5

/**
 * 최근 검사 스트립(인메모리·휘발). 영속 당일 목록("오늘 이력")이 진실 원천이며
 * 이것은 연속 플로우 중 직전 몇 건의 즉각 피드백용 보조 표시다(새로고침 시 소멸).
 */
export function InspectionHistory({ items }: { items: InspectResult[] }) {
  const recent = items.slice(0, RECENT_LIMIT)
  if (recent.length === 0) return null

  return (
    <div className="absolute bottom-4 right-4 w-44 rounded-xl bg-black/60 p-2 text-xs text-white backdrop-blur">
      <div className="px-1 pb-1 font-semibold text-white/70">최근</div>
      <ul className="space-y-1">
        {recent.map((it) => (
          <li
            key={it.image_id}
            className="flex items-center justify-between rounded-md bg-white/5 px-2 py-1"
          >
            <span className={it.verdict === 'NG' ? 'text-red-400' : 'text-emerald-400'}>
              {it.verdict}
            </span>
            <span className="text-white/50">{formatTime(it.timestamp)}</span>
          </li>
        ))}
      </ul>
    </div>
  )
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return d.toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}
