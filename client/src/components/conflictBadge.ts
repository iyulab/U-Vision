import { isConflicted } from '../lib/audit'
import type { StoredLabel } from '../lib/types'

/** 결과표 충돌 배지 문구(C1) — 미해소 충돌만. 라벨을 다시 달면 서버가 resolved 로 닫는다. */
export function conflictBadgeLabel(label: StoredLabel | undefined): string | null {
  return isConflicted(label) ? '⚠ 충돌' : null
}
