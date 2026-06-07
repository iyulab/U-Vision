"""VLM 벤치마크 하네스 — M0.1(VLM 왕복 실측)의 실행 경로.

이미지 폴더를 provider 로 판정하며 latency / 정확도 / 비용 참조를 markdown 으로 낸다.

키가 없으면 mock 으로 **흐름만** 시연된다(정확도/비용 무의미).
키가 있으면 동일 명령이 실 provider 로 **실측**을 수행한다 — 이것이 M0.1 이다.

사용법:
  # mock 시연
  python -m benchmark.run_benchmark --images-dir ./benchmark/sample-images

  # 실측 (키 확보 후) — ground truth 는 images-dir/{ok,ng}/ 하위폴더로 라벨링
  VLM_API_KEY=sk-... python -m benchmark.run_benchmark \
      --images-dir ./samples --provider openai --model gpt-4o \
      --out ../claudedocs/spikes/vlm-benchmark.md

ground truth 라벨:
  images-dir/ok/*.jpg  → 정답 OK
  images-dir/ng/*.jpg  → 정답 NG
  images-dir/*.jpg     → 라벨 없음(정확도 미산출, latency 만)
"""

from __future__ import annotations

import argparse
import asyncio
import statistics
import time
from dataclasses import dataclass
from pathlib import Path

from app.core.config import Settings
from app.models.inspection import ScenarioContext, Verdict
from app.services.vlm import get_provider

_IMAGE_EXTS = {".jpg", ".jpeg", ".png"}

# USD per 1M tokens — 2025/2026 공개가 기준(참고용). 실 토큰은 provider usage 로 채운다.
_PRICING: dict[str, tuple[float, float]] = {
    "gpt-4o": (2.50, 10.00),
    "gemini-2.5-flash": (0.30, 2.50),
    "gemini-1.5-pro": (1.25, 5.00),
}

_DEFAULT_CRITERIA = (
    "제품 표면에 긁힘, 이물질, 균열, 솔더 브릿지 등 외관 결함이 없어야 한다. "
    "결함이 보이면 NG, 깨끗하면 OK."
)


@dataclass
class Row:
    name: str
    truth: Verdict | None
    verdict: Verdict
    confidence: float
    latency_ms: float


def discover_images(images_dir: Path) -> list[tuple[Path, Verdict | None]]:
    items: list[tuple[Path, Verdict | None]] = []
    for label, verdict in (("ok", Verdict.OK), ("ng", Verdict.NG)):
        sub = images_dir / label
        if sub.is_dir():
            items += [(p, verdict) for p in sorted(sub.iterdir()) if p.suffix.lower() in _IMAGE_EXTS]
    # 라벨 없는 flat 이미지
    items += [(p, None) for p in sorted(images_dir.iterdir()) if p.suffix.lower() in _IMAGE_EXTS]
    return items


async def run(args: argparse.Namespace) -> str:
    images = discover_images(Path(args.images_dir))
    if not images:
        raise SystemExit(f"이미지를 찾지 못함: {args.images_dir}")

    cfg = Settings(
        vlm_provider=args.provider,
        vlm_model=args.model,
        vlm_api_key=Settings().vlm_api_key,  # .env/환경에서
    )
    provider = get_provider(cfg)
    scenario = ScenarioContext(scenario_id="benchmark", criteria=args.criteria)

    rows: list[Row] = []
    for path, truth in images:
        data = path.read_bytes()
        t0 = time.perf_counter()
        result = await provider.inspect(data, scenario)
        latency = (time.perf_counter() - t0) * 1000.0
        rows.append(Row(path.name, truth, result.verdict, result.confidence, latency))

    report = render_report(rows, provider.name, cfg.vlm_model)
    print(report)
    if args.out:
        out = Path(args.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(report, encoding="utf-8")
        print(f"\n→ 리포트 저장: {out}")
    return report


def render_report(rows: list[Row], provider: str, model: str) -> str:
    latencies = [r.latency_ms for r in rows]
    labeled = [r for r in rows if r.truth is not None]
    p50 = statistics.median(latencies)
    p95 = sorted(latencies)[min(len(latencies) - 1, int(len(latencies) * 0.95))]

    lines = [
        "# VLM 벤치마크 리포트",
        "",
        f"- provider: `{provider}` / model: `{model}`",
        f"- 이미지 수: {len(rows)} (라벨됨 {len(labeled)})",
        f"- latency: p50 **{p50:.0f} ms**, p95 **{p95:.0f} ms**, "
        f"min {min(latencies):.0f} / max {max(latencies):.0f}",
    ]

    if provider == "mock":
        lines += [
            "",
            "> ⚠️ **mock provider** — 이미지를 보지 않는다. 아래 정확도/판정은 "
            "**파이프라인 흐름 시연일 뿐 의미 없음**. 실측은 키 확보 후 "
            "`--provider openai|google` 로 재실행(= M0.1).",
        ]

    if labeled:
        tp = sum(1 for r in labeled if r.truth == Verdict.NG and r.verdict == Verdict.NG)
        tn = sum(1 for r in labeled if r.truth == Verdict.OK and r.verdict == Verdict.OK)
        fp = sum(1 for r in labeled if r.truth == Verdict.OK and r.verdict == Verdict.NG)
        fn = sum(1 for r in labeled if r.truth == Verdict.NG and r.verdict == Verdict.OK)
        acc = (tp + tn) / len(labeled)
        precision = tp / (tp + fp) if (tp + fp) else 0.0
        recall = tp / (tp + fn) if (tp + fn) else 0.0
        f1 = 2 * precision * recall / (precision + recall) if (precision + recall) else 0.0
        lines += [
            "",
            "## 정확도 (NG = positive)",
            "",
            f"- accuracy **{acc:.3f}** / precision {precision:.3f} / recall {recall:.3f} / F1 **{f1:.3f}**",
            f"- confusion: TP(NG→NG) {tp}, TN(OK→OK) {tn}, FP(OK→NG) {fp}, FN(NG→OK) {fn}",
        ]

    lines += [
        "",
        "## 건별 결과",
        "",
        "| 이미지 | 정답 | 판정 | confidence | latency(ms) |",
        "|---|---|---|---|---|",
    ]
    for r in rows:
        truth = r.truth.value if r.truth else "-"
        lines.append(
            f"| {r.name} | {truth} | {r.verdict.value} | {r.confidence:.2f} | {r.latency_ms:.0f} |"
        )

    price = _PRICING.get(model)
    lines += [
        "",
        "## 비용 참조",
        "",
        "건별 실 토큰은 provider usage 로 채워야 한다(키 확보 후 TODO). 단가(USD/1M tok):",
        "",
        "| model | input | output |",
        "|---|---|---|",
    ]
    for m, (pin, pout) in _PRICING.items():
        mark = " ←" if m == model else ""
        lines.append(f"| {m} | ${pin:.2f} | ${pout:.2f}{mark}")
    if price is None:
        lines.append(f"\n> `{model}` 단가 미등록 — 표에 추가 필요.")

    return "\n".join(lines) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser(description="VLM 벤치마크 하네스 (M0.1)")
    parser.add_argument("--images-dir", required=True, help="이미지 폴더(ok/ ng/ 하위폴더로 라벨)")
    parser.add_argument("--provider", default="mock", help="mock | openai | google")
    parser.add_argument("--model", default="gpt-4o")
    parser.add_argument("--criteria", default=_DEFAULT_CRITERIA, help="판정 기준(자연어)")
    parser.add_argument("--out", default=None, help="markdown 리포트 저장 경로")
    asyncio.run(run(parser.parse_args()))


if __name__ == "__main__":
    main()
