import { fraction } from './metrics'
import { sameClass } from './labels'
import type { ClassificationMetrics } from './types'

/** 혼동 행렬의 한 줄 — 정답(사람 라벨)이 이 클래스인 판정들이 어느 클래스로 판정됐는가. */
export interface ConfusionRow {
  /** 정답(사람 라벨) 클래스. */
  truth: string
  /** `ConfusionTable.classes` 와 같은 순서로, 이 정답이 그 열 클래스로 판정된 건수. */
  cells: number[]
  total: number
  /** 이 정답 가운데 맞게 판정된 비율(대각 / 줄 합). 줄 합 0 이면 null — 위장하지 않는다. */
  recall: number | null
}

export interface ConfusionTable {
  /** 행 · 열 클래스 — 같은 순서로 쓴다. */
  classes: string[]
  rows: ConfusionRow[]
}

/**
 * 서버 혼동 행렬(정답 → 예측 → 건수)을 표로. 행 · 열 클래스는 한 목록이다 — 시나리오가 선언한 순서(`declared`)를 먼저, 그 밖에서
 * 나온 클래스(선언에서 빠졌거나 대소문자만 다른 철자)는 뒤에 이름 순으로 잇는다. 정답으로만 나왔거나 예측으로만 나온 클래스도
 * 둘 다에 있어야 대각선이 맞는다. 선언했지만 한 번도 안 나온 클래스는 뺀다(전부 0 인 줄 · 열은 읽기만 늘린다). 사람 라벨이 붙은
 * 판정이 없으면 빈 표.
 */
export function confusionTable(
  c: Pick<ClassificationMetrics, 'confusion' | 'predicted'>,
  declared: string[] = [],
): ConfusionTable {
  const seen = new Set<string>()
  for (const [truth, row] of Object.entries(c.confusion)) {
    seen.add(truth)
    for (const predicted of Object.keys(row)) seen.add(predicted)
  }
  if (seen.size === 0) return { classes: [], rows: [] }
  const classes: string[] = []
  const take = (id: string) => {
    const hit = [...seen].find((s) => sameClass(s, id))
    if (hit !== undefined && !classes.some((k) => sameClass(k, hit))) classes.push(hit)
  }
  declared.forEach(take)
  ;[...seen].sort().forEach(take)
  const rows = classes.map((truth) => {
    const source = Object.entries(c.confusion).filter(([k]) => sameClass(k, truth))
    const cells = classes.map((predicted) =>
      source.reduce((n, [, row]) => n + Object.entries(row).filter(([p]) => sameClass(p, predicted)).reduce((m, [, v]) => m + v, 0), 0),
    )
    const total = cells.reduce((a, b) => a + b, 0)
    const diagonal = cells[classes.indexOf(truth)]
    return { truth, cells, total, recall: total > 0 ? diagonal / total : null }
  })
  return { classes, rows }
}

/** 판정 분포(많은 순, 같으면 이름 순) — 서버 `predicted` 는 이름 순이라 화면용으로 다시 센다. */
export function predictedRows(c: Pick<ClassificationMetrics, 'predicted'>): { id: string; count: number }[] {
  return Object.entries(c.predicted)
    .map(([id, count]) => ({ id, count }))
    .sort((a, b) => b.count - a.count || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
}

/** 사람 라벨이 이만큼 안 쌓이면 일치율은 참고만 — 학습 준비의 «클래스당 30건» 과 같은 값(서버 기준값을 화면이 되풀이한다). */
export const AGREEMENT_RELIABLE_LABELS = 30

/**
 * 일치율 위에 다는 정직 문구 — 믿을 만하면 null. 라벨이 없으면 일치율을 «0%» 로 위장하지 않고 무엇이 모자란지를 말한다.
 * 이 일치율은 «사람 라벨 대비 판정» 이라 라벨러의 편향까지 담는다(참값이 아니다).
 */
export function agreementNotice(c: Pick<ClassificationMetrics, 'checked' | 'labeled'>): string | null {
  if (c.checked === 0) return null
  if (c.labeled === 0)
    return `판정 ${c.checked}건에 사람 라벨이 아직 없어 일치율 · 혼동 행렬을 낼 수 없습니다 — 결과 조회에서 라벨을 붙이면 채워집니다.`
  if (c.labeled < AGREEMENT_RELIABLE_LABELS)
    return `사람 라벨 ${c.labeled}건뿐이라 일치율은 참고만 하세요 — ${AGREEMENT_RELIABLE_LABELS}건 안팎이 쌓이기 전에는 크게 흔들립니다.`
  return null
}

/** 일치율 셀의 보조 글 — «12/15 일치». */
export function agreementSub(c: Pick<ClassificationMetrics, 'agreed' | 'labeled'>): string {
  return `${fraction(c.agreed, c.labeled)} 일치`
}

