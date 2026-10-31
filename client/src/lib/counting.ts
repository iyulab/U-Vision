import type { Scenario } from './types'

/** 서버 기대 개수 · 허용 오차 상한과 같다 — 화면 · 검증용 방어선. */
export const MAX_COUNT = 100_000

/** 세는 물건 종류 한 이름의 길이 상한(서버와 같다)과 개수 상한. */
const KIND_MAX_LENGTH = 64
const KIND_MAX_COUNT = 64

type CountScenario = Pick<
  Scenario,
  'expected_count' | 'count_tolerance' | 'expected_by_class' | 'extra_policy' | 'min_confidence' | 'count_classes'
>

/** 기대보다 많거나 기대에 없는 종류를 어떻게 볼 것인가 — 서버 `extra_policy` 와 같은 어휘. */
export type ExtraPolicy = 'ng' | 'warn' | 'ignore'

export const EXTRA_POLICIES: { value: ExtraPolicy; label: string }[] = [
  { value: 'ng', label: '불량으로 본다 (기본)' },
  { value: 'warn', label: '항목에는 올리되 판정은 그대로' },
  { value: 'ignore', label: '보지 않는다' },
]

const EXTRA_SHORT: Record<ExtraPolicy, string> = { ng: '불량', warn: '경고', ignore: '무시' }

/** 기대를 어떻게 적는가 — 센 총 개수를 견주거나(total), 종류마다 기대를 둔다(by_class, 구성 확인). 서버는 둘 중 하나만 받는다. */
export type CountMode = 'total' | 'by_class'

/** 계수 시나리오 입력란 — 사용자가 치는 대로(문자열)다. 비어 있음이 뜻을 가지므로 숫자로 바꾸지 않는다. */
export interface CountDraft {
  mode: CountMode
  /** 총 개수 기대(mode=total). */
  expected: string
  /** 허용 오차(mode=total 에서만 뜻이 있다 — 종류별 기대에는 허용 오차가 없다). */
  tolerance: string
  /** 종류별 기대(mode=by_class) — 한 줄(또는 쉼표)에 «종류=개수» 하나. */
  byClass: string
  extraPolicy: ExtraPolicy
  minConfidence: string
  /** 세는 물건의 종류(검출기 라벨), 쉼표 · 줄바꿈 구분 — 비우면 검출된 전부를 센다. */
  kinds: string
}

export const EMPTY_COUNT_DRAFT: CountDraft = {
  mode: 'total',
  expected: '',
  tolerance: '',
  byClass: '',
  extraPolicy: 'ng',
  minConfidence: '',
  kinds: '',
}

/** 서버로 보내는 계수 필드. 기대는 한 모양만 간다 — 서버는 한쪽을 받으면 다른 쪽(과 거기 묶인 필드)을 지운다. */
export type CountFields = {
  /** 비우면 생략 — 서버는 생략한 값을 저장된 그대로 둔다(설치 기본으로 돌리는 길은 없다). */
  min_confidence?: number
  count_classes: string[]
} & (
  | { expected_count: number; count_tolerance: number }
  | { expected_by_class: Record<string, number>; extra_policy: ExtraPolicy }
)

/** 저장된 시나리오 → 입력란(`parseCountDraft` 의 역). */
export function countDraftOf(scenario: CountScenario): CountDraft {
  const byClass = scenario.expected_by_class
  return {
    mode: byClass === undefined ? 'total' : 'by_class',
    expected: scenario.expected_count === undefined ? '' : String(scenario.expected_count),
    tolerance: scenario.count_tolerance === undefined ? '' : String(scenario.count_tolerance),
    byClass: byClass === undefined ? '' : Object.entries(byClass).map(([kind, n]) => `${kind}=${n}`).join('\n'),
    extraPolicy: isExtraPolicy(scenario.extra_policy) ? scenario.extra_policy : 'ng',
    minConfidence: scenario.min_confidence === undefined ? '' : String(scenario.min_confidence),
    kinds: (scenario.count_classes ?? []).join(', '),
  }
}

function isExtraPolicy(value: string | undefined): value is ExtraPolicy {
  return value === 'ng' || value === 'warn' || value === 'ignore'
}

function wholeNumber(text: string): number | null {
  return /^\d+$/.test(text) ? Number(text) : null
}

/** 종류 이름 한 칸의 규칙 — `count_classes` 와 같다(제어문자 없는 1~64자). */
function kindNameError(name: string): string | null {
  return name.length < 1 || name.length > KIND_MAX_LENGTH || /\p{C}/u.test(name)
    ? `종류 이름은 제어문자 없는 1~${KIND_MAX_LENGTH}자`
    : null
}

/** «종류=개수» 줄들 → 맵. 개수 앞의 마지막 `=` 로 나눈다(종류 이름에 `=` 가 들어 있어도 읽힌다). */
function parseByClass(text: string): { expected: Record<string, number> } | { error: string } {
  const lines = text.split(/[,\n]/).map((s) => s.trim()).filter((s) => s !== '')
  if (lines.length === 0) return { error: '종류별 기대를 한 줄에 하나씩 «종류=개수» 로 입력하세요' }
  if (lines.length > KIND_MAX_COUNT) return { error: `기대 종류는 ${KIND_MAX_COUNT}개 이하` }
  const expected: Record<string, number> = {}
  for (const line of lines) {
    const cut = line.lastIndexOf('=')
    if (cut < 0) return { error: `«종류=개수» 형식이 아니다: ${line}` }
    const name = line.slice(0, cut).trim()
    const nameError = kindNameError(name)
    if (nameError) return { error: nameError }
    const count = wholeNumber(line.slice(cut + 1).trim())
    if (count === null || count > MAX_COUNT) return { error: `${name} 의 개수는 0~${MAX_COUNT} 의 정수` }
    if (Object.hasOwn(expected, name)) return { error: `기대 종류가 겹친다: ${name}` }
    expected[name] = count
  }
  return { expected }
}

/**
 * 입력란을 서버 필드로. 서버가 거부할 입력(기대 없음 · 범위 밖 · 겹치는 종류 · 세는 종류가 기대 종류를 빠뜨림)은 여기서 사유와
 * 함께 막는다. 허용 오차를 비우면 0(정확히)이고, 최소 신뢰도를 비우면 보내지 않는다.
 */
export function parseCountDraft(draft: CountDraft): { fields: CountFields } | { error: string } {
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

  const shared = {
    ...(minConfidence === undefined ? {} : { min_confidence: minConfidence }),
    count_classes: kinds,
  }

  if (draft.mode === 'by_class') {
    const parsed = parseByClass(draft.byClass)
    if ('error' in parsed) return parsed
    // 세는 종류를 좁히면 기대 종류가 센 결과에 못 들어와 언제나 누락이 된다 — 서버도 같은 이유로 거부한다.
    const left = Object.keys(parsed.expected).filter((k) => kinds.length > 0 && !kinds.includes(k))
    if (left.length > 0) return { error: `세는 종류가 기대 종류를 빠뜨렸다: ${left.join(', ')} (비우거나 모두 포함하세요)` }
    return { fields: { ...shared, expected_by_class: parsed.expected, extra_policy: draft.extraPolicy } }
  }

  const expectedText = draft.expected.trim()
  if (expectedText === '') return { error: '기대 개수를 입력하세요' }
  const expected = wholeNumber(expectedText)
  if (expected === null || expected > MAX_COUNT) return { error: `기대 개수는 0~${MAX_COUNT} 의 정수` }

  const toleranceText = draft.tolerance.trim()
  const tolerance = toleranceText === '' ? 0 : wholeNumber(toleranceText)
  if (tolerance === null || tolerance > MAX_COUNT) return { error: `허용 오차는 0~${MAX_COUNT} 의 정수` }

  return { fields: { ...shared, expected_count: expected, count_tolerance: tolerance } }
}

/** 계수 시나리오인가 — OK/NG 가 센 개수에서 나온 파생값이라 사진 하나로 묻는 문제(사진 확인)를 내지 않는다. */
export function isCounting(scenario: Pick<Scenario, 'type'>): boolean {
  return scenario.type === 'counting'
}

/** 시나리오 목록 한 줄 — «기대 6 ± 1 · 신뢰도 0.7 이상 · helmet» / «구성 scalpel 2 · forceps 1 · 과잉 불량». 계수가 아니면 null. */
export function countLine(scenario: Pick<Scenario, 'type'> & CountScenario): string | null {
  if (scenario.type !== 'counting') return null
  const parts: string[] = []
  if (scenario.expected_by_class !== undefined) {
    const kinds = Object.entries(scenario.expected_by_class).map(([k, n]) => `${k} ${n}`).join(' · ')
    parts.push(`구성 ${kinds}`)
    parts.push(`과잉 ${EXTRA_SHORT[isExtraPolicy(scenario.extra_policy) ? scenario.extra_policy : 'ng']}`)
  } else {
    parts.push(`기대 ${scenario.expected_count ?? '?'} ± ${scenario.count_tolerance ?? 0}`)
  }
  if (scenario.min_confidence !== undefined) parts.push(`신뢰도 ${scenario.min_confidence} 이상`)
  if (scenario.count_classes?.length) parts.push(scenario.count_classes.join(' · '))
  return parts.join(' · ')
}
