import { classNameOf, operativeLabel } from './labels'
import type { Scenario, StoredLabel, StoredResult } from './types'
import { termOf } from './verification'

/**
 * 검사 결과 CSV — 결과 조회 화면이 불러온 레코드와 사람 라벨을 그대로 옮긴다(서버 왕복 없음).
 * 열 이름은 API 필드 이름을 따르고, 값은 저장값 그대로다. 오투입 확인이면 판정을 일치/불일치로 읽은 열을, 불량 유형을
 * 나눈 레코드가 있으면 `class`(id) 와 `class_label`(표시 이름, 없으면 id) 열을 더한다.
 * 스프레드시트에서 바로 열리도록 UTF-8 BOM · CRLF 를 쓰고, 셀이 수식으로 실행되지 않게 수식 시작 문자를 막는다.
 */
export function resultsToCsv(
  results: StoredResult[],
  labels: Map<string, StoredLabel>,
  scenario?: Pick<Scenario, 'type' | 'classes'>,
): string {
  const scenarioType = scenario?.type
  const verification = scenarioType === 'verification'
  const nameOf = classNameOf(scenario)
  const typed = results.some((r) => r.class !== undefined)
  const header = [
    'image_id',
    'timestamp',
    'verdict',
    ...(typed ? ['class', 'class_label'] : []),
    ...(verification ? ['verdict_term', 'expected_part', 'observed_code'] : []),
    'confidence',
    'findings',
    'device_label',
    'device_id',
    'ml_label',
    'ml_confidence',
    'agreement',
    'requires_review',
    'human_label',
  ]
  const rows = results.map((r) => [
    r.image_id,
    r.timestamp,
    r.verdict,
    ...(typed ? [r.class, r.class === undefined ? undefined : nameOf(r.class)] : []),
    ...(verification ? [termOf(r.verdict, scenarioType), r.expected_part, r.observed_code] : []),
    r.confidence,
    r.findings,
    r.device_label,
    r.device_id,
    r.ml?.label,
    r.ml?.confidence,
    r.agreement,
    r.requires_review,
    operativeLabel(labels.get(r.image_id)),
  ])
  return '﻿' + [header, ...rows].map((row) => row.map(cell).join(',')).join('\r\n') + '\r\n'
}

/** 한 셀 — 빈 값은 빈 칸, 쉼표 · 따옴표 · 줄바꿈은 따옴표로 감싼다. */
function cell(value: string | number | boolean | undefined | null): string {
  if (value === undefined || value === null) return ''
  let s = String(value)
  // 수식 주입 — 스프레드시트가 =, +, -, @, 탭, CR 로 시작하는 셀을 수식으로 해석한다. 숫자는 그대로 둔다.
  if (typeof value === 'string' && /^[=+\-@\t\r]/.test(s)) s = `'${s}`
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
}

/** 파일 이름 — 시나리오 · 날짜. 둘 다 서버가 화이트리스트로 거른 id 라 그대로 쓴다. */
export function csvFileName(scenarioId: string, date: string): string {
  return `u-vision-${scenarioId}-${date}.csv`
}

/** 브라우저 다운로드 — Blob 링크를 눌러 저장하고 바로 놓는다. */
export function downloadCsv(fileName: string, csv: string): void {
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  // 클릭이 내려받기를 시작한 뒤에 놓는다 — 같은 틱에 놓으면 브라우저에 따라 내려받기가 취소된다.
  setTimeout(() => URL.revokeObjectURL(url), 0)
}
