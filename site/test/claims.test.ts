import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { COPY } from '../src/copy';

const siteDir = fileURLToPath(new URL('..', import.meta.url));
const repoRoot = path.resolve(siteDir, '..');
const STATUSES = new Set(['works', 'design', 'vision', 'promise']);

interface Row {
  claim: string;
  status: string;
  refs: string[];
}

function readClaims(): Row[] {
  const lines = readFileSync(path.join(siteDir, 'claims.tsv'), 'utf8')
    .split(/\r?\n/)
    .filter(line => line !== '' && !line.startsWith('#'));
  const [header, ...rows] = lines;
  expect(header.split('\t')).toEqual(['claim', 'status', 'ref', 'context']);
  return rows.map(line => {
    const [claim, status, ref] = line.split('\t');
    return { claim, status, refs: ref.split(' · ') };
  });
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(dir, entry.name);
    return entry.isDirectory() ? sourceFiles(full) : /\.(astro|ts)$/.test(entry.name) ? [full] : [];
  });
}

/** Every claim id the site's sources name: `data-claim="…"` in markup, `claim: '…'` in the copy. */
function claimIdsInSource(): Set<string> {
  const ids = new Set<string>();
  for (const file of sourceFiles(path.join(siteDir, 'src'))) {
    const text = readFileSync(file, 'utf8');
    for (const m of text.matchAll(/data-claim="([\w-]+)"/g)) ids.add(m[1]);
    for (const m of text.matchAll(/claim: '([\w-]+)'/g)) ids.add(m[1]);
  }
  return ids;
}

describe('public claims', () => {
  const rows = readClaims();

  it('lists every claim the site makes, and nothing it does not', () => {
    expect([...claimIdsInSource()].sort()).toEqual(rows.map(r => r.claim).sort());
  });

  it('gives each claim one row, a known status and refs that exist in this repository', () => {
    expect(new Set(rows.map(r => r.claim)).size).toBe(rows.length);
    for (const row of rows) {
      expect(STATUSES, row.claim).toContain(row.status);
      for (const ref of row.refs) expect(existsSync(path.join(repoRoot, ref)), `${row.claim}: ${ref}`).toBe(true);
    }
  });

  // A goal must never read as something that works today: the page shows the roadmap label on exactly the
  // statements whose row says vision, in both languages.
  it('marks exactly the vision claims as roadmap', () => {
    const vision = rows.filter(r => r.status === 'vision').map(r => r.claim).sort();
    for (const locale of ['ko', 'en'] as const) {
      const marked = new Set<string>();
      const walk = (node: unknown): void => {
        if (Array.isArray(node)) return node.forEach(walk);
        if (node === null || typeof node !== 'object') return;
        const record = node as Record<string, unknown>;
        if (typeof record.claim === 'string' && record.roadmap === true) marked.add(record.claim);
        Object.values(record).forEach(walk);
      };
      walk(COPY[locale]);
      expect([...marked].sort(), locale).toEqual(vision);
    }
  });
});

describe('cookies', () => {
  it('nothing in the site sets a cookie', () => {
    for (const file of sourceFiles(path.join(siteDir, 'src'))) {
      expect(readFileSync(file, 'utf8'), file).not.toMatch(/document\.cookie|Set-Cookie/i);
    }
  });
});

// What is not built yet is marked «in development» and said in the future tense. The page does not call it a roadmap,
// and does not call its pictures samples or mock-ups: they show what the product does or is being built to do.
describe('wording', () => {
  const strings = (node: unknown): string[] =>
    typeof node === 'string'
      ? [node]
      : node !== null && typeof node === 'object'
        ? Object.values(node as Record<string, unknown>).flatMap(strings)
        : [];

  it('the page copy names no roadmap, sample or mock-up', () => {
    for (const locale of ['ko', 'en'] as const) {
      for (const text of strings(COPY[locale])) {
        expect(text, locale).not.toMatch(/로드맵|시안|샘플|예시|\broadmap\b|\bsamples?\b|\bmock-?ups?\b/i);
      }
    }
  });

  it('no studio scene writes those words into a picture', () => {
    const scenes = path.join(siteDir, 'studio', 'scenes');
    for (const file of readdirSync(scenes).filter(f => f.endsWith('.html'))) {
      expect(readFileSync(path.join(scenes, file), 'utf8'), file).not.toMatch(/로드맵|시안|샘플|예시/);
    }
  });
});
