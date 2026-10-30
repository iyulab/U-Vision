import { ApiError, NetworkError } from './api'
import type { CaptureQueue, QueuedCapture } from './captureQueue'

/** 큐 항목 하나를 보낸 결말. */
export type SendOutcome<R> =
  | { kind: 'sent'; item: QueuedCapture; result: R }
  /** 서버가 답했지만 실패(검증 400 · 판정 불가 503 …) — 서버의 결정이므로 다시 보내지 않고 큐에서 뺀다. */
  | { kind: 'failed'; item: QueuedCapture; error: unknown }
  /**
   * 서버가 답했지만 아무것도 결정하지 못했다(저장 실패 500 · 게이트웨이 502/503/504) — 항목은 큐에 남아 다시 보낸다.
   * 연결이 끊긴 것이 아니라서 `drain` 은 멈추지 않고 다음 항목으로 넘어간다(한 항목이 줄을 막지 않게).
   */
  | { kind: 'retry'; item: QueuedCapture; error: unknown }
  /** 서버에 닿지 못했다 — 항목은 큐에 남는다. */
  | { kind: 'offline' }
  /** 이미 다른 보내기가 처리했다. */
  | { kind: 'gone' }

/**
 * 서버가 요청을 받았지만 판정 · 기록을 하지 못한 응답인가 — 같은 캡처를 다시 보내도 안전하다(`Idempotency-Key` 가 중복을 막고
 * 서버는 200 응답만 키별로 저장한다). 판정 불가(503 + `detection_unavailable`)는 `DetectionUnavailableError` 로 따로 오므로
 * 여기 속하지 않는다 — 그것은 서버의 결정(fail-closed)이라 다시 보내지 않는다.
 */
function isRetryable(error: unknown): boolean {
  return error instanceof ApiError && [500, 502, 503, 504].includes(error.status)
}

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
        if (isRetryable(error)) return { kind: 'retry', item, error }
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
