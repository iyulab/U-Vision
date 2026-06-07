import type { InspectResult } from '../lib/types'

/** 현재 세션 검사 이력(최신순). 인메모리 — 새로고침 시 소멸(C6). */
export function InspectionHistory({ items }: { items: InspectResult[] }) {
  if (items.length === 0) return null

  return (
    <div className="absolute bottom-4 right-4 max-h-[40vh] w-44 overflow-y-auto rounded-xl bg-black/60 p-2 text-xs text-white backdrop-blur">
      <div className="px-1 pb-1 font-semibold text-white/70">세션 이력</div>
      <ul className="space-y-1">
        {items.map((it) => (
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
