/**
 * 시나리오 id — 데이터 폴더 · 판정 사건 · CSV 에 남고 만든 뒤 바뀌지 않는다. 비우면 서버가 이름의 영문 · 숫자에서
 * 만드는데(서버 `FileScenarioStore.Slugify` 와 같은 규칙), 한글 이름은 영문이 남지 않아 `scenario` · 숫자만 남는다.
 */

/** 서버가 이름에서 만들 id 의 바탕(겹치면 서버가 `-2` … 를 붙인다). */
export function derivedScenarioId(name: string): string {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
  return slug === '' ? 'scenario' : slug
}

/** 이름에서 만든 id 로는 어느 검사인지 알 수 없는가 — 이름에 영문 글자가 없으면 id 를 직접 정하라고 권한다. */
export function needsExplicitScenarioId(name: string): boolean {
  return name.trim() !== '' && !/[a-z]/i.test(name)
}

/** 서버가 받는 id 인가 — 영문 · 숫자 · `. _ -`, 128자 이하, `.` · `..` 아님. */
export function isValidScenarioId(id: string): boolean {
  return id.length > 0 && id.length <= 128 && id !== '.' && id !== '..' && /^[A-Za-z0-9._-]+$/.test(id)
}
