import { CLASS_ID, CLASS_LABEL } from './defectClasses'
import type { Scenario, ScenarioClass } from './types'

/** 서버 클래스 설명 길이 상한(`EffectiveScenario.MaxClassDescription`)과 같다 — VLM 프롬프트에 실리므로 짧게 닫는다. */
export const MAX_CLASS_DESCRIPTION = 400

/** 서버 클래스 설명 규칙과 같다 — 앞뒤 공백 · 제어문자(줄바꿈 포함) 없이 1~400자. */
const CLASS_DESCRIPTION = /^(?=\S)[^\p{C}]{1,400}(?<=\S)$/u

/**
 * 다중 분류(`classification`) 시나리오인가 — 정상이 여럿이거나 품번 · 품종 · 판독처럼 값 중립인 클래스 중 하나를 고르는 유형.
 * 서버는 결과 v2 로만 이 유형을 받는다(v1 `/inspect` 는 거절).
 */
export function isClassification(scenario: Pick<Scenario, 'type'>): boolean {
  return scenario.type === 'classification'
}

/** 클래스 편집기의 한 줄 — 사용자가 치는 대로(문자열)다. 비어 있음이 «생략» 이라 숫자 · null 로 바꾸지 않는다. */
export interface ClassDraft {
  id: string
  label: string
  description: string
  /** 이 클래스를 아래 단(전용 ML · 기억)이 확정해도 되는가. */
  settleable: boolean
  /** 놓치면 안 되는 클래스인가 — 결과의 OK/NG 보기와 NG 재현율이 이 값을 따른다. */
  mustCatch: boolean
  /** 수정을 시작할 때 이미 저장돼 있던 클래스 — id 는 기준 이미지 폴더 · 기억의 이름이라 그 뒤로 고치지 않는다. */
  saved: boolean
}

/** 새 시나리오의 시작 모양 — 둘 이상이 필요하니 빈 줄 둘. */
export function emptyClassDrafts(): ClassDraft[] {
  return [blankClass(), blankClass()]
}

export function blankClass(): ClassDraft {
  return { id: '', label: '', description: '', settleable: false, mustCatch: false, saved: false }
}

/** 저장된 시나리오 → 편집기 줄. 선별이 아니면 빈 줄 둘(유형을 선별로 바꿀 때의 시작점). */
export function classDraftsOf(scenario: Pick<Scenario, 'type' | 'classes'>): ClassDraft[] {
  if (!isClassification(scenario) || !scenario.classes?.length) return emptyClassDrafts()
  return scenario.classes.map((c) => ({
    id: c.id,
    label: c.label ?? '',
    description: c.description ?? '',
    settleable: c.settleable === true,
    mustCatch: c.must_catch === true,
    saved: true,
  }))
}

/**
 * 서버가 거부할 입력을 보내기 전에 사유와 함께 막는다 — 사유가 없으면 null. 서버 `EffectiveScenario.Validate` 의
 * 선별 분기와 같은 규칙이다(둘 이상 · id 형식 · 겹침 · 표시 이름 · 설명 · settleable 과 must_catch 동시 금지).
 */
export function classDraftsError(drafts: ClassDraft[]): string | null {
  if (drafts.length < 2) return '클래스는 둘 이상이어야 한다'
  for (const d of drafts) {
    const id = d.id.trim()
    if (!CLASS_ID.test(id)) return `클래스 id 는 영문 · 숫자 · '_' · '-' 로 64자까지: ${id === '' ? '(빈 칸)' : id}`
    const label = d.label.trim()
    if (label !== '' && !CLASS_LABEL.test(label)) return `표시 이름은 1~32자, ',' · '=' 없이: ${id}`
    const description = d.description.trim()
    if (description !== '' && !CLASS_DESCRIPTION.test(description))
      return `설명은 ${MAX_CLASS_DESCRIPTION}자까지, 줄바꿈 없이: ${id}`
    if (d.settleable && d.mustCatch) return `확정 허용과 꼭 잡을 값은 함께 켤 수 없다: ${id}`
  }
  const ids = drafts.map((d) => d.id.trim().toLowerCase())
  if (new Set(ids).size !== ids.length) return '클래스 id 가 겹친다(대소문자 무시)'
  const labels = drafts.map((d) => d.label.trim().toLowerCase()).filter((l) => l !== '')
  if (new Set(labels).size !== labels.length) return '표시 이름이 겹친다'
  return null
}

/** 편집기 줄 → 시나리오 `classes`. 비운 표시 이름 · 설명은 보내지 않고, 꺼진 플래그도 보내지 않는다(저장 파일을 간결하게). */
export function classesOfDrafts(drafts: ClassDraft[]): ScenarioClass[] {
  return drafts.map((d) => {
    const label = d.label.trim()
    const description = d.description.trim()
    return {
      id: d.id.trim(),
      ...(label !== '' ? { label } : {}),
      ...(description !== '' ? { description } : {}),
      ...(d.settleable ? { settleable: true } : {}),
      ...(d.mustCatch ? { must_catch: true } : {}),
    }
  })
}

/** 저장된 시나리오와 편집기 줄이 같은가 — 같으면 classes 를 다시 보내지 않는다(서버는 생략하면 저장된 값을 그대로 둔다). */
export function sameClassDrafts(a: ClassDraft[], b: ClassDraft[]): boolean {
  return JSON.stringify(classesOfDrafts(a)) === JSON.stringify(classesOfDrafts(b))
}

/** 목록 배지용 요약 — «품번 A · 품번 B» 처럼 표시 이름이 있으면 그 이름. */
export function classSummary(scenario: Pick<Scenario, 'classes'>): string {
  return (scenario.classes ?? []).map((c) => c.label ?? c.id).join(' · ')
}
