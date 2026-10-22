"""Render the site's pictures and video from studio/scenes into public/media.

    python studio/capture.py            # every studio slot
    python studio/capture.py operator   # just these slots

Needs Python Playwright (with Chromium) and Pillow; the video also needs ffmpeg on PATH. A slot whose origin in
src/media.json is "field" is a real photo or recording and is never overwritten.
"""

import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image
from playwright.sync_api import sync_playwright

STUDIO = Path(__file__).resolve().parent
SITE = STUDIO.parent
OUT = SITE / "public" / "media"
SLOTS = json.loads((SITE / "src" / "media.json").read_text(encoding="utf-8"))["slots"]

SMALL_WIDTH = 800  # the second size every picture gets, for narrow screens
FPS = 30


def save_image(png: Path, slot: str) -> None:
    image = Image.open(png)
    image.save(OUT / f"{slot}.webp", quality=84, method=6)
    small = image.resize((SMALL_WIDTH, round(image.height * SMALL_WIDTH / image.width)), Image.LANCZOS)
    small.save(OUT / f"{slot}-{SMALL_WIDTH}.webp", quality=82, method=6)


def shoot_image(page, slot: str, work: Path) -> None:
    page.goto((STUDIO / "scenes" / f"{slot}.html").as_uri())
    page.wait_for_load_state("networkidle")
    png = work / f"{slot}.png"
    page.locator("#shot").screenshot(path=str(png), omit_background=True)
    save_image(png, slot)


def shoot_video(page, slot: str, work: Path) -> None:
    page.goto((STUDIO / "scenes" / f"{slot}.html").as_uri())
    page.wait_for_load_state("networkidle")
    duration = page.evaluate("window.DURATION")
    frames = work / slot
    frames.mkdir()
    shot = page.locator("#shot")
    for i in range(round(duration * FPS)):
        page.evaluate(f"window.seek({i / FPS})")
        shot.screenshot(path=str(frames / f"{i:05d}.png"))
    pattern = str(frames / "%05d.png")
    common = ["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", pattern]
    subprocess.run(
        [*common, "-c:v", "libx264", "-preset", "slow", "-crf", "24", "-pix_fmt", "yuv420p",
         "-movflags", "+faststart", "-an", str(OUT / f"{slot}.mp4")],
        check=True,
    )
    subprocess.run(
        [*common, "-c:v", "libvpx-vp9", "-crf", "36", "-b:v", "0", "-row-mt", "1", "-an", str(OUT / f"{slot}.webm")],
        check=True,
    )
    # the poster is the frame the page shows before the video plays, and to anyone who prefers reduced motion
    poster = frames / "poster.png"
    page.evaluate("window.seek(6)")
    shot.screenshot(path=str(poster))
    Image.open(poster).convert("RGB").save(OUT / f"{slot}-poster.webp", quality=84, method=6)


def main(wanted: list[str]) -> None:
    unknown = [s for s in wanted if s not in SLOTS]
    if unknown:
        sys.exit(f"unknown slot: {', '.join(unknown)}")
    if not shutil.which("ffmpeg") and any(SLOTS[s]["kind"] == "video" for s in wanted or SLOTS):
        sys.exit("ffmpeg is not on PATH")
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp, sync_playwright() as pw:
        work = Path(tmp)
        browser = pw.chromium.launch()
        page = browser.new_page(viewport={"width": 1800, "height": 1200}, device_scale_factor=1)
        for slot, meta in SLOTS.items():
            if wanted and slot not in wanted:
                continue
            if meta["origin"] != "studio":
                print(f"{slot}: {meta['origin']}, kept")
                continue
            (shoot_video if meta["kind"] == "video" else shoot_image)(page, slot, work)
            print(f"{slot}: rendered")
        browser.close()


if __name__ == "__main__":
    main(sys.argv[1:])
