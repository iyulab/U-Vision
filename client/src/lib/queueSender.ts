import { NetworkError } from './api'
import type { CaptureQueue, QueuedCapture } from './captureQueue'

/** 큐 항목 하나를 보낸 결말. */
export type SendOutcome<R> =
  | { kind: 'sent'; item: QueuedCapture; result: R }
  /** 서버가 답했지만 실패(검증 400 · 판정 불가 503 …) — 서버의 결정이므로 다시 보내지 않고 큐에서 뺀다. */
  | { kind: 'failed'; item: QueuedCapture; error: unknown }
  /** 서버에 닿지 못했다 — 항목은 큐에 남는다. */
  | { kind: 'offline' }
  /** 이미 다른 보내기가 처리했다. */
  | { kind: 'gone' }

/**
 * 큐에서 꺼내 보내는 단일 통로 — 모든 보내기를 한 줄로 세워(동시에 하나) 같은 항목이 두 번 가지 않게 한다.
 * 방금 찍은 캡처는 `sendOne` 으로 바로, 쌓인 캡처는 `drain` 으로 먼저 찍은 것부터 보낸다.
 */
export function createQueueSender<R>(queue: CaptureQueue, send: (item: QueuedCapture) => Promise<R>) {
  let chain: Promise<unknown> = Promise.resolve()

  const exclusive = <T>(fn: () => Promise<T>): Promise<T> => {
    const run = chain.then(fn, fn)
    chain = run.catch(() => undefined)
    return run
  }

  const sendOne = (id: string): Promise<SendOutcome<R>> =>
    exclusive(async () => {
      const item = await queue.get(id)
      if (!item) return { kind: 'gone' }
      try {
        const result = await send(item)
        await queue.remove(id)
        return { kind: 'sent', item, result }
      } catch (error) {
        if (error instanceof NetworkError) return { kind: 'offline' }
        await queue.remove(id)
        return { kind: 'failed', item, error }
      }
    })

  /** 쌓인 항목을 먼저 찍은 것부터 보낸다. 연결이 다시 끊기면 멈춘다. 보낸 항목마다 `onOutcome`. */
  const drain = async (onOutcome: (o: SendOutcome<R>) => void): Promise<void> => {
    for (const id of await queue.ids()) {
      const outcome = await sendOne(id)
      if (outcome.kind === 'offline') return
      onOutcome(outcome)
    }
  }

  return { sendOne, drain }
}
