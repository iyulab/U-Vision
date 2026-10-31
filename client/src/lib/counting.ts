import type { Scenario } from './types'

/** 서버 기대 개수 · 허용 오차 상한과 같다 — 화면 · 검증용 방어선. */
export const MAX_COUNT = 100_000

/** 세는 물건 종류 한 이름의 길이 상한(서버와 같다)과 개수 상한. */
const KIND_MAX_LENGTH = 64
const KIND_MAX_COUNT = 64

type CountScenario = Pick<Scenario, 'expected_count' | 'count_tolerance' | 'min_confidence' | 'count_classes'>

/** 계수 시나리오 입력란 — 사용자가 치는 대로(문자열)다. 비어 있음이 뜻을 가지므로 숫자로 바꾸지 않는다. */
export interface CountDraft {
  expected: string
  tolerance: string
  minConfidence: string
  /** 세는 물건의 종류(검출기 라벨), 쉼표 · 줄바꿈 구분 — 비우면 검출된 전부를 센다. */
  kinds: string
}

export const EMPTY_COUNT_DRAFT: CountDraft = { expected: '', tolerance: '', minConfidence: '', kinds: '' }

/** 서버로 보내는 계수 필드. */
export interface CountFields {
  expected_count: number
  count_tolerance: number
  /** 비우면 생략 — 서버는 생략한 값을 저장된 그대로 둔다(설치 기본으로 돌리는 길은 없다). */
  min_confidence?: number
  count_classes: string[]
}

/** 저장된 시나리오 → 입력란(`parseCountDraft` 의 역). */
export function countDraftOf(scenario: CountScenario): CountDraft {
  return {
    expected: scenario.expected_count === undefined ? '' : String(scenario.expected_count),
    tolerance: scenario.count_tolerance === undefined ? '' : String(scenario.count_tolerance),
    minConfidence: scenario.min_confidence === undefined ? '' : String(scenario.min_confidence),
    kinds: (scenario.count_classes ?? []).join(', '),
  }
}

function wholeNumber(text: string): number | null {
  return /^\d+$/.test(text) ? Number(text) : null
}

/**
 * 입력란을 서버 필드로. 서버가 거부할 입력(기대 개수 없음 · 범위 밖 · 겹치는 종류)은 여기서 사유와 함께 막는다.
 * 허용 오차를 비우면 0(정확히)이고, 최소 신뢰도를 비우면 보내지 않는다.
 */
export function parseCountDraft(draft: CountDraft): { fields: CountFields } | { error: string } {
  const expectedText = draft.expected.trim()
  if (expectedText === '') return { error: '기대 개수를 입력하세요' }
  const expected = wholeNumber(expectedText)
  if (expected === null || expected > MAX_COUNT) return { error: `기대 개수는 0~${MAX_COUNT} 의 정수` }

  const toleranceText = draft.tolerance.trim()
  const tolerance = toleranceText === '' ? 0 : wholeNumber(toleranceText)
  if (tolerance === null || tolerance > MAX_COUNT) return { error: `허용 오차는 0~${MAX_COUNT} 의 정수` }

  const confidenceText = draft.minConfidence.trim()
  let minConfidence: number | undefined
  if (confidenceText !== '') {
    const value = Number(confidenceText)
    if (!Number.isFinite(value) || !(value > 0 && value <= 1)) return { error: '최소 신뢰도는 0 초과 1 이하' }
    minConfidence = value
  }

  const kinds = draft.kinds.split(/[,\n]/).map((s) => s.trim()).filter((s) => s !== '')
  if (kinds.length > KIND_MAX_COUNT) return { error: `세는 종류는 ${KIND_MAX_COUNT}개 이하` }
  if (kinds.some((k) => k.length > KIND_MAX_LENGTH || /\p{C}/u.test(k))) {
    return { error: `종류 이름은 제어문자 없는 1~${KIND_MAX_LENGTH}자` }
  }
  if (new Set(kinds).size !== kinds.length) return { error: '세는 종류가 겹친다' }

  return {
    fields: {
      expected_count: expected,
      count_tolerance: tolerance,
      ...(minConfidence === undefined ? {} : { min_confidence: minConfidence }),
      count_classes: kinds,
    },
  }
}

/** 시나리오 목록 한 줄 — «기대 6 ± 1 · 신뢰도 0.7 이상 · helmet». 계수가 아니면 null. */
export function countLine(scenario: Pick<Scenario, 'type'> & CountScenario): string | null {
  if (scenario.type !== 'counting') return null
  const parts = [`기대 ${scenario.expected_count ?? '?'} ± ${scenario.count_tolerance ?? 0}`]
  if (scenario.min_confidence !== undefined) parts.push(`신뢰도 ${scenario.min_confidence} 이상`)
  if (scenario.count_classes?.length) parts.push(scenario.count_classes.join(' · '))
  return parts.join(' · ')
}
