import { describe, expect, it } from 'vitest'

import { compositionLine, countingDatasetNotice, kindRows, unavailableNote } from './countingMetrics'

describe('kindRows', () => {
  it('두 사전의 종류를 합치고 어긋난 건이 많은 종류를 위로 둔다(같으면 이름 순)', () => {
    const rows = kindRows({
      missing_by_kind: { scalpel: 2, forceps: 1 },
      extra_by_kind: { clamp: 1, forceps: 2 },
    })

    expect(rows).toEqual([
      { kind: 'forceps', missing: 1, extra: 2 }, // 3
      { kind: 'scalpel', missing: 2, extra: 0 }, // 2
      { kind: 'clamp', missing: 0, extra: 1 }, // 1
    ])
  })

  it('동률은 이름 순이라 서버가 사전 순서를 바꿔도 화면이 흔들리지 않는다', () => {
    const rows = kindRows({ missing_by_kind: { b: 1, a: 1 }, extra_by_kind: {} })

    expect(rows.map((r) => r.kind)).toEqual(['a', 'b'])
  })

  it('어긋난 종류가 없으면 빈 배열', () => {
    expect(kindRows({ missing_by_kind: {}, extra_by_kind: {} })).toEqual([])
  })
})

describe('countingDatasetNotice', () => {
  it('주입이 없으면 null', () => {
    expect(countingDatasetNotice({ checked: 5, dataset_checked: 0 })).toBeNull()
  })

  it('전부 주입이면 현장 수치가 아니라고, 일부면 섞여 있다고 말한다', () => {
    expect(countingDatasetNotice({ checked: 5, dataset_checked: 5 })).toContain('현장 촬영 수치가 아닙니다')
    expect(countingDatasetNotice({ checked: 5, dataset_checked: 2 })).toContain('섞여 있습니다')
  })
})

describe('compositionLine', () => {
  it('구성 확인이 없으면 null — 총 개수 기대뿐인 시나리오에 종류별 줄이 생기지 않는다', () => {
    expect(
      compositionLine({ composition_checked: 0, with_missing: 0, with_extra: 0, missing_rate: null, extra_rate: null }),
    ).toBeNull()
  })

  it('모자란 건과 과잉인 건을 구성 확인 건 분모로 보이고 합이 분모와 같지 않을 수 있음을 숫자가 보인다', () => {
    const line = compositionLine({ composition_checked: 4, with_missing: 3, with_extra: 2, missing_rate: 0.75, extra_rate: 0.5 })

    expect(line).toContain('구성 확인 4건')
    expect(line).toContain('3/4 (75%)')
    expect(line).toContain('2/4 (50%)')
  })
})

describe('unavailableNote', () => {
  it('불능이 없으면 null', () => {
    expect(unavailableNote({ unavailable: 0 })).toBeNull()
  })

  it('불능이 있으면 개수를 모르는 채 통과시키지 않았다고 말한다', () => {
    expect(unavailableNote({ unavailable: 2 })).toContain('통과시키지 않았습니다')
  })
})
