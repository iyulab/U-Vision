// The pictures and video the site shows, read from media.json (the register studio/capture.py also reads).
// Files live in public/media: <slot>.webp and <slot>-800.webp for a picture, <slot>.mp4, <slot>.webm and
// <slot>-poster.webp for a video.

import register from './media.json';
import type { Locale } from './copy';

export type SlotId = keyof typeof register.slots;

export interface Slot {
  kind: 'image' | 'video';
  /** studio = rendered from studio/scenes; field = a real photo or recording */
  origin: 'studio' | 'field';
  width: number;
  height: number;
  alt: Record<Locale, string>;
}

export const SLOTS = register.slots as Record<SlotId, Slot>;

export const SMALL_WIDTH = 800;

export const mediaPath = (file: string) => `/media/${file}`;

/** Every file a slot needs, relative to public/. */
export function slotFiles(id: SlotId): string[] {
  return SLOTS[id].kind === 'video'
    ? [`media/${id}.mp4`, `media/${id}.webm`, `media/${id}-poster.webp`]
    : [`media/${id}.webp`, `media/${id}-${SMALL_WIDTH}.webp`];
}
