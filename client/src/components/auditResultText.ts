/** 블라인드 감사 결과 문구(C1) — 제출 후 일치/충돌 표시. */
export function auditResultText(status: string): { text: string; ok: boolean } {
  if (status === 'consistent') return { text: '✓ 일관됨', ok: true }
  if (status === 'conflicted') return { text: '⚠ 충돌 — 검토 필요', ok: false }
  return { text: status, ok: false }
}
