# Changelog

Changes to the `UVision.Hosting` NuGet package. Versions 0.2.0 and 0.2.1 were never published; their changes ship in 0.3.0.

## [Unreleased]

### Added
- The admin console footer shows the U-Platform affiliation element (`@uplatform/brand` 0.4.0): one line linking to the U-Platform family, with the other products in a drop-down. It makes no network requests and sets no cookies.
- Decision ledger. Every inspection appends one line to `{DataPath}/{scenario}/ledger/{yyyy-MM-dd}.jsonl`: a HoneAI `PredictionProvenance` from a tiered router over the answers the inspection already received (ML below, VLM on top), with the verdict actually returned recorded beside it (`operative:*` annotations). The router only observes: with no trust policy yet, every slice is in shadow, so the VLM settles and responses are unchanged. A ledger write failure is logged and does not affect the inspection. Adds a dependency on `HoneAI.Core` 0.9.0, which has no further dependencies.
- Scenario definition v2 fields, all optional: `type`, `trigger`, `classes` (each with `settleable` / `must_catch`), `judges` (tier order, top last) and `trust` (`slice_key`, `targets`). A scenario without them reads as today's OK/NG inspection, and they are omitted from `scenario.json` and responses when unset, so existing scenarios are unchanged byte for byte. The decision ledger now takes its settlement policy, slice key and tier order from the scenario. Only values that are built are accepted (`type: inspection`, `trigger: still`, tiers `ml` and `vlm` with `vlm` on top, `slice_key: scenario-v1`); anything else is rejected with `400`. Updating a scenario without v2 fields keeps the stored ones.
- `POST {ApiBasePath}/v2/inspect`: the same request, decision and records as `/inspect`, answered in a v2 shape — `class` (a scenario class id), `settled_by` (the tier that decided), `confidence`, `findings` (when the VLM decided), `requires_review` (when two judges ran) and `path`, the router's per-tier outcome (`settled`, `shadowed`, `escalated`, `abstained`). When no decision can be made it returns `503` with `detection_unavailable`, `reason` and `path`. The `/inspect` response is unchanged.
- Part verification scenarios (`type: verification`, classes `MATCH` / `MISMATCH`). `/v2/inspect` takes `expected_part` and, when the capturing device decoded one, `observed_code`. A deterministic match tier compares the two below the vision model (it answers `MATCH`/`MISMATCH`, or abstains without a code); the vision model is told the expected part and its OK/NG answer is read as `MATCH`/`MISMATCH`. A verification scenario requires `expected_part`, so it cannot be called through `/inspect`. Stored results gain `expected_part` and `observed_code` when they were sent. The dedicated ML classifier is called only when the scenario's tiers include it.
- The operator screen supports part verification scenarios: an "expected part" field appears for them, captures go to `/v2/inspect` with the code read by the browser's `BarcodeDetector` when one is available, and the result shows as match / mismatch. The admin console can set a scenario's type. Changing a scenario's type drops its stored classes and tiers instead of carrying them over.
- Results views (results table, detail, today's list, label audit) read OK/NG as match / mismatch for verification scenarios, and the detail shows the expected part and the code read. Stored values stay OK/NG.
- CSV export from the results view: the rows on screen (including the review-only filter) with their human labels, as UTF-8 with a BOM so spreadsheets open it directly. Cells that a spreadsheet would run as a formula are prefixed with `'`. Verification scenarios add the match / mismatch reading, the expected part and the code read.

### Fixed
- In manual capture mode the result badge sat under the shutter button, hiding the findings and confidence. The badge now sits above it.
- Identifiers (scenario, reference, model version, export ids) with a trailing newline passed validation, because the pattern was anchored with `$`, and could become directory names. They are now rejected.
- `201 Created` responses (scenario, reference image, model version, dataset export) returned a `Location` under `/api/u-vision` even when the host mounted the API at another `ApiBasePath`; the dataset export's `Location` also lacked the API base. `Location` now follows `ApiBasePath` and the request's `PathBase`.
- Concurrent writes to one image's label sidecar — a human label and an oracle opinion, or an audit — could drop an event or fail on the file rename. Writes to the same sidecar are now serialized.
- A PNG upload was sent to the vision model labeled as JPEG when server-side downscaling (`max_image_dimension`) is off. The image format sent to the model is now read from the image bytes.

### Changed
- The inspection pipeline (reference loading, model calls, verdict decision, record, metrics) moved out of the `/inspect` HTTP handler into `InspectionPipeline`, registered by `AddUVision`. Responses, stored records and metrics rows are unchanged byte for byte.
- `ImageUpload.ExtensionFor` moved to `ImageFiles.ExtensionFor` (`UVision.Api.Imaging`).

## [0.3.0] - 2026-10-22

### Security
- Removed `SixLabors.ImageSharp`. Version 3.1.12 carries three high-severity advisories (GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w) and two moderate ones (GHSA-gwg2-r3hj-4w44, GHSA-wmxv-xphr-5c9g). Restores with NuGet audit no longer report NU1902/NU1903 for this package.
- Upgraded IronHive to 0.68.0, which clears the transitive MessagePack advisory GHSA-qhrr-8q5h-9q3h. The transitive Scriban and SQLitePCLRaw advisories that 0.1.x carried through IronHive 0.7.4 are also gone.

### Added
- First-run admin PIN setup. When no PIN is configured, `GET /api/u-vision/admin/status` reports `setup_required: true` and `POST /api/u-vision/admin/setup` sets the PIN with a one-time token written to the data directory (`admin-setup-token.txt`). The PIN is stored as a PBKDF2-SHA256 hash. A PIN set through configuration takes precedence and disables this path.

### Changed
- Image decoding and JPEG encoding now use SkiaSharp 4.153.1 (MIT). Server-side downscaling (`max_image_dimension`) uses a built-in separable bicubic resampler whose output matches the previous implementation within 1 level per channel on average. The re-encoded JPEG can differ slightly from earlier versions because the JPEG encoder changed.
- Native assets: Windows and macOS natives come with `SkiaSharp`; Linux natives (glibc and musl, x64 and arm64) come with `SkiaSharp.NativeAssets.Linux.NoDependencies`, which needs no fontconfig. Consumers add nothing per platform. If your app already references `SkiaSharp.NativeAssets.Linux`, `dotnet publish` reports a duplicate `libSkiaSharp.so`; keep one package and exclude the other with `ExcludeAssets="all"`.
- IronHive 0.68.0 requires `Microsoft.Extensions.*` 10.0.13 or later. A host that pins an older 10.0.x version of those packages gets NU1109 on restore; raise the pin.
- Accepted upload formats are unchanged (JPEG and PNG).

## [0.1.1] - 2026-06

- Consumer integration guide, MIT license, minimal host sample.

## [0.1.0] - 2026-06

- First release: embeddable `AddUVision` / `UseUVision` with the built PWA.
