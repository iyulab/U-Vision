import { useCallback, useEffect, useRef, useState } from 'react'

import type { PinRunResult } from '../hooks/usePin'
import { listEdgeNodeEvents } from '../lib/api'
import { clock, describeCurrent, describeEvent, type CurrentLine, type Tone } from '../lib/edgeEvents'
import type { EdgeNodeEvents } from '../lib/types'

type RunWithPin = (action: (pin: string) => Promise<void>) => Promise<PinRunResult>

/** 열려 있는 동안 몇 초마다 다시 읽는가 — 영상이 흐르는 동안 센 값이 바뀌는 것을 따라가려는 값이다. */
export const POLL_MS = 5000

const TONE_STYLE: Record<Tone, string> = {
  ok: 'text-emerald-300',
  warn: 'text-amber-300',
  bad: 'text-red-300',
  muted: 'text-slate-300',
}

/**
 * 노드가 보낸 사건 — 규칙별 «지금 센 값 · 경보 · 소스 상태» 와 최근 사건 목록(최신 먼저). 읽기 전용이고 판정에 쓰이지 않는다.
 * 열면 주기적으로 다시 읽는다. PIN 을 주지 않으면(취소 · 불일치) 다시 묻지 않고 닫는다 — 주기 읽기가 PIN 창을 반복해 띄우면 안 된다.
 * 값은 서버가 사건 장부에서 파생한 것을 그대로 보이며, 소스가 정상이 아니면 센 값은 지우지 않고 «낡음» 으로 표시한다.
 */
export function EdgeNodeEventsSection({ nodeId, runWithPin }: { nodeId: string; runWithPin: RunWithPin }) {
  const [open, setOpen] = useState(false)
  const [view, setView] = useState<EdgeNodeEvents | null>(null)
  const [readAt, setReadAt] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  // 주기 읽기는 오래 산 타이머가 부르므로 최신 러너를 ref 로 든다(렌더 중 ref 를 쓰지 않고 effect 에서 갱신).
  const runRef = useRef(runWithPin)
  useEffect(() => {
    runRef.current = runWithPin
  })
  const inFlight = useRef(false)

  const load = useCallback(async () => {
    if (inFlight.current) return
    inFlight.current = true
    try {
      const result = await runRef.current(async (pin) => {
        const v = await listEdgeNodeEvents(nodeId, pin, 50)
        setView(v)
        setReadAt(new Date().toISOString())
        setError(null)
      })
      if (result !== 'ok') {
        setOpen(false)
        setError(result === 'unauthorized' ? 'PIN 이 올바르지 않습니다.' : null)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : '사건을 읽지 못했습니다')
    } finally {
      inFlight.current = false
    }
  }, [nodeId])

  useEffect(() => {
    if (!open) return
    // 첫 읽기도 타이머로 — 효과 안에서 상태를 동기로 바꾸는 호출을 만들지 않는다.
    const first = setTimeout(() => void load(), 0)
    const timer = setInterval(() => void load(), POLL_MS)
    return () => {
      clearTimeout(first)
      clearInterval(timer)
    }
  }, [open, load])

  const current = view ? describeCurrent(view) : null
  const events = view ? view.events.map(describeEvent) : []
  const none = current !== null && current.counts.length + current.alarms.length + current.sources.length === 0 && events.length === 0

  return (
    <section className="space-y-2 border-t border-slate-700 pt-2" aria-label="센 값 · 사건">
      <div className="flex flex-wrap items-center gap-2">
        <button
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
          className="rounded-lg bg-slate-700 px-3 py-1.5 text-sm text-slate-100 hover:bg-slate-600"
        >
          {open ? '센 값 · 사건 닫기' : '센 값 · 사건 보기'}
        </button>
        {open && readAt && (
          <span className="text-xs text-slate-500" data-testid="edge-events-read">
            {POLL_MS / 1000}초마다 갱신 · 마지막으로 읽은 시각 {clock(readAt)}
          </span>
        )}
      </div>
      {error && <p className="text-sm text-red-300">{error}</p>}

      {open && view && current && (
        <div className="space-y-3">
          {none ? (
            <p className="text-sm text-slate-400">아직 이 노드에서 온 사건이 없습니다.</p>
          ) : (
            <>
              <LineList title="센 값" testId="edge-current-counts" lines={current.counts} />
              <LineList title="경보" testId="edge-current-alarms" lines={current.alarms} />
              <LineList title="소스(카메라) 상태" testId="edge-current-sources" lines={current.sources} />
              <div className="space-y-1">
                <h3 className="text-xs font-semibold text-slate-400">최근 사건 ({events.length}건, 최신 먼저)</h3>
                <ol className="space-y-0.5 text-sm" data-testid="edge-events">
                  {events.map((e) => (
                    <li key={e.key} className={`flex gap-2 ${TONE_STYLE[e.tone]}`}>
                      <span className="shrink-0 tabular-nums text-slate-500">{e.time}</span>
                      <span className="min-w-0 break-words">{e.text}</span>
                    </li>
                  ))}
                </ol>
              </div>
              {view.truncated && (
                <p className="text-xs text-amber-300">
                  읽기 한도({view.scanned}건)에 걸려 더 오래된 사건은 읽지 않았습니다 — 오래 전에 한 번만 난 규칙의 상태는 빠졌을 수 있습니다.
                </p>
              )}
            </>
          )}
        </div>
      )}
    </section>
  )
}

function LineList({ title, testId, lines }: { title: string; testId: string; lines: CurrentLine[] }) {
  if (lines.length === 0) return null
  return (
    <div className="space-y-1">
      <h3 className="text-xs font-semibold text-slate-400">{title}</h3>
      <ul className="space-y-0.5 text-sm" data-testid={testId}>
        {lines.map((l) => (
          <li key={l.key} className={`break-words ${TONE_STYLE[l.tone]}`} data-stale={l.stale ? 'true' : 'false'}>
            {l.text}
          </li>
        ))}
      </ul>
    </div>
  )
}
