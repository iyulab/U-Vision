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

/** 같은 항목이 서버에서 이만큼 연달아 받아들여지지 않으면 «계속 받지 못하는» 캡처로 센다. */
export const STUCK_AFTER_FAILURES = 5
const RETRY_BASE_MS = 15_000
const RETRY_MAX_MS = 10 * 60_000

/**
 * 같은 항목이 서버에서 `failures` 번째로 받아들여지지 않은 뒤 다시 보내기까지 기다릴 시간 — 15초에서 시작해 두 배씩 늘리고 10분에서
 * 멈춘다. 서버가 매번 같은 5xx 로 거절하는 캡처(깨진 입력 · 저장소 문제)가 15초마다 영원히 서버를 두드리지 않게 한다.
 */
export function retryDelayMs(failures: number): number {
  return Math.min(RETRY_BASE_MS * 2 ** Math.max(0, failures - 1), RETRY_MAX_MS)
}

/** 보내지 못하고 남은 캡처가 있다는 알림 문구. 없으면 null. */
export function stuckNotice(count: number): string | null {
  return count > 0 ? `서버가 캡처 ${count}건을 계속 받지 못하고 있습니다 — 기기에 남겨 두고 간격을 늘려 다시 보냅니다. 오래 가면 관리자에게 알려 주세요.` : null
}

/** `stuckNotice` 로 만든 문구인가 — 더는 막힌 항목이 없을 때 그 문구만 걷어 낸다. */
export function isStuckNotice(text: string | null): boolean {
  return text !== null && text.startsWith('서버가 캡처 ') && text.includes('계속 받지 못하고 있습니다')
}

/**
 * 큐에서 꺼내 보내는 단일 통로 — 모든 보내기를 한 줄로 세워(동시에 하나) 같은 항목이 두 번 가지 않게 한다.
 * 방금 찍은 캡처는 `sendOne` 으로 바로, 쌓인 캡처는 `drain` 으로 먼저 찍은 것부터 보낸다.
 */
export function createQueueSender<R>(
  queue: CaptureQueue,
  send: (item: QueuedCapture) => Promise<R>,
  now: () => number = Date.now,
) {
  let chain: Promise<unknown> = Promise.resolve()
  /** 받아들여지지 않은 항목의 연속 실패 횟수와 다음 시도 시각(이 페이지 수명 동안만 — 새로고침하면 처음부터 다시 센다). */
  const failures = new Map<string, { count: number; nextAt: number }>()

  const exclusive = <T>(fn: () => Promise<T>): Promise<T> => {
    const run = chain.then(fn, fn)
    chain = run.catch(() => undefined)
    return run
  }

  const sendOne = (id: string): Promise<SendOutcome<R>> =>
    exclusive(async () => {
      const item = await queue.get(id)
      if (!item) {
        failures.delete(id)
        return { kind: 'gone' }
      }
      try {
        const result = await send(item)
        await queue.remove(id)
        failures.delete(id)
        return { kind: 'sent', item, result }
      } catch (error) {
        if (error instanceof NetworkError) return { kind: 'offline' }
        if (isRetryable(error)) {
          const count = (failures.get(id)?.count ?? 0) + 1
          failures.set(id, { count, nextAt: now() + retryDelayMs(count) })
          return { kind: 'retry', item, error }
        }
        await queue.remove(id)
        failures.delete(id)
        return { kind: 'failed', item, error }
      }
    })

  /**
   * 쌓인 항목을 먼저 찍은 것부터 보낸다. 연결이 다시 끊기면 멈춘다. 보낸 항목마다 `onOutcome`.
   * 서버가 받아들이지 않은 항목은 다음 시도 시각(`retryDelayMs`)이 오기 전에는 건너뛴다 — 주기 재시도가 같은 항목을 15초마다
   * 두드리지 않게. `force`(연결이 돌아왔다는 신호처럼 «지금 해 보라» 는 계기)는 기다리지 않는다.
   */
  const drain = async (onOutcome: (o: SendOutcome<R>) => void, options: { force?: boolean } = {}): Promise<void> => {
    for (const id of await queue.ids()) {
      const backoff = failures.get(id)
      if (!options.force && backoff && now() < backoff.nextAt) continue
      const outcome = await sendOne(id)
      if (outcome.kind === 'offline') return
      onOutcome(outcome)
    }
  }

  /** 연달아 `STUCK_AFTER_FAILURES` 번 이상 받아들여지지 않은 항목 수. */
  const stuckCount = (): number => [...failures.values()].filter((f) => f.count >= STUCK_AFTER_FAILURES).length

  return { sendOne, drain, stuckCount }
}
