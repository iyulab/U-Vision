import { describe, it, expect } from 'vitest';
import { existsSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { SLOTS, slotFiles, type SlotId } from '../src/media';

const publicDir = fileURLToPath(new URL('../public', import.meta.url));
const ids = Object.keys(SLOTS) as SlotId[];

describe('media', () => {
  it('has every file each slot needs', () => {
    for (const id of ids) {
      for (const file of slotFiles(id)) expect(existsSync(path.join(publicDir, file)), file).toBe(true);
    }
  });

  it('publishes nothing in public/media that no slot names', () => {
    const named = new Set(ids.flatMap(slotFiles));
    for (const file of readdirSync(path.join(publicDir, 'media'))) expect(named, file).toContain(`media/${file}`);
  });

  it('describes every slot in both languages', () => {
    for (const id of ids) {
      expect(SLOTS[id].alt.ko, id).not.toBe('');
      expect(SLOTS[id].alt.en, id).not.toBe('');
      expect(['studio', 'field'], id).toContain(SLOTS[id].origin);
    }
  });
});
