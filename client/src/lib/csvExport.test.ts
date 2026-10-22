import { describe, expect, it } from 'vitest'

import { csvFileName, resultsToCsv } from './csvExport'
import type { StoredLabel, StoredResult } from './types'

const base: StoredResult = {
  scenario_id: 's',
  image_id: 'img_1',
  verdict: 'NG',
  findings: '좌측, "긁힘"\n2곳',
  confidence: 0.91,
  timestamp: '2026-10-22T09:00:00Z',
  image_file: 'img_1.jpg',
  device_id: 'dev',
  device_label: '라인1',
}

describe('resultsToCsv', () => {
  it('BOM · CRLF · 헤더, 쉼표 · 따옴표 · 줄바꿈은 감싼다', () => {
    const csv = resultsToCsv([base], new Map())
    expect(csv.startsWith('﻿image_id,timestamp,verdict,confidence,')).toBe(true)
    expect(csv.endsWith('\r\n')).toBe(true)
    expect(csv).toContain('"좌측, ""긁힘""\n2곳"')
  })

  it('사람 라벨 · ML 열을 채운다', () => {
    const labels = new Map<string, StoredLabel>([['img_1', { image_id: 'img_1', label: 'OK', timestamp: 't' }]])
    const row = resultsToCsv([{ ...base, findings: '', ml: { label: 'ng', confidence: 0.8 }, agreement: true }], labels)
      .split('\r\n')[1]
    expect(row).toBe('img_1,2026-10-22T09:00:00Z,NG,0.91,,라인1,dev,ng,0.8,true,,OK')
  })

  it('오투입 확인이면 일치/불일치 · 기대 품번 · 읽은 코드 열', () => {
    const csv = resultsToCsv([{ ...base, findings: '', expected_part: 'P-100', observed_code: 'P-200' }], new Map(), { type: 'verification' })
    const [header, row] = csv.slice(1).split('\r\n')
    expect(header).toContain('verdict,verdict_term,expected_part,observed_code,confidence')
    expect(row).toContain('NG,불일치,P-100,P-200,0.91')
  })

  it('불량 유형을 나눈 레코드가 있으면 class 열', () => {
    const scenario = { classes: [{ id: 'OK' }, { id: 'scratch', label: '긁힘' }, { id: 'dent' }] }
    const csv = resultsToCsv(
      [
        { ...base, findings: '', class: 'scratch' },
        { ...base, findings: '', class: 'dent' },
      ],
      new Map(),
      scenario,
    )
    const [header, scratch, dent] = csv.slice(1).split('\r\n')
    expect(header).toContain('verdict,class,class_label,confidence')
    expect(scratch).toContain('NG,scratch,긁힘,0.91')
    expect(dent).toContain('NG,dent,dent,0.91')
  })

  it('수식으로 실행될 셀은 막는다', () => {
    const row = resultsToCsv([{ ...base, findings: '=HYPERLINK("x")' }], new Map()).split('\r\n')[1]
    expect(row).toContain(`"'=HYPERLINK(""x"")"`)
  })

  it('파일 이름', () => {
    expect(csvFileName('pcb', '2026-10-22')).toBe('u-vision-pcb-2026-10-22.csv')
  })
})
