# Studio — the site's pictures and video

The screens on the site are drawn here as HTML, in the client's own visual language, and rendered into
`public/media/`. One scene is one slot in `src/media.json`; the slot's `alt` text is what the page says about it.

```
studio/
├── assets/        photos the scenes put inside the camera views
├── studio.css     the client's colors and controls, by hand
├── scenes/        <slot>.html — a picture is the #shot element; hero.html is a video, drawn by seek(t)
└── capture.py     renders scenes into public/media
```

## Render

Needs Python with Playwright (Chromium installed) and Pillow; the video also needs ffmpeg on PATH.

```bash
python studio/capture.py            # every studio slot
python studio/capture.py operator   # just these slots
```

A picture becomes `<slot>.webp` and `<slot>-800.webp`; the video becomes `hero.mp4`, `hero.webm` and
`hero-poster.webp` (the frame at 6 s). `npm test` checks that every slot has its files and that `public/media`
holds nothing else.

## Replace a slot with the real thing

When a real photo or recording of the screen exists, put it at the slot's paths, set the slot's `origin` to
`field` and its `width`/`height` to the new size, and update `alt` if what it shows changed. `capture.py` never
overwrites a `field` slot, so the scene can stay as the reference for the next round.

## Rules for scenes

- Draw what the product shows, or — for a slot behind a roadmap section — what it is being built to show. A
  screen of something that works today must not show controls or data that the product does not have.
- Part numbers, scenario names, times and counts are made up and neutral; no customer, site or person names.
- Photos in `assets/` are generated, contain no real people or products, and carry no text or logos.
