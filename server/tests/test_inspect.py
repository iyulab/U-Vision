"""inspect 엔드포인트 + mock provider 테스트.

provider 는 기본 설정(vlm_provider="mock")으로 구동되므로 키 불필요.
⚠️ 이 테스트는 plumbing(파이프라인 흐름)을 검증한다. VLM 판정 정확도는
검증하지 않는다(M0.1 영역) — mock 은 이미지를 보지 않는다.
"""

import asyncio

from fastapi.testclient import TestClient

from app.main import app
from app.models.inspection import ScenarioContext, Verdict
from app.services.vlm.mock import MockProvider

client = TestClient(app)

_JPEG = ("capture.jpg", b"\xff\xd8\xff\xe0\x00\x10JFIF payload", "image/jpeg")


def test_mock_provider_is_deterministic() -> None:
    provider = MockProvider()
    scenario = ScenarioContext(scenario_id="demo")
    r1 = asyncio.run(provider.inspect(b"identical-bytes", scenario))
    r2 = asyncio.run(provider.inspect(b"identical-bytes", scenario))
    assert r1 == r2
    assert r1.verdict in (Verdict.OK, Verdict.NG)
    assert 0.0 <= r1.confidence <= 1.0


def test_health() -> None:
    resp = client.get("/api/health")
    assert resp.status_code == 200
    assert resp.json()["status"] == "ok"


def test_inspect_returns_verdict() -> None:
    resp = client.post(
        "/api/inspect", files={"image": _JPEG}, data={"scenario_id": "demo"}
    )
    assert resp.status_code == 200
    body = resp.json()
    assert body["verdict"] in ("OK", "NG")
    assert body["image_id"].startswith("img_")
    assert body["timestamp"]
    assert 0.0 <= body["confidence"] <= 1.0


def test_inspect_rejects_non_image() -> None:
    resp = client.post(
        "/api/inspect",
        files={"image": ("note.txt", b"hello", "text/plain")},
        data={"scenario_id": "demo"},
    )
    assert resp.status_code == 415


def test_inspect_rejects_unknown_scenario() -> None:
    resp = client.post(
        "/api/inspect", files={"image": _JPEG}, data={"scenario_id": "ghost"}
    )
    assert resp.status_code == 404


def test_inspect_rejects_empty_image() -> None:
    resp = client.post(
        "/api/inspect",
        files={"image": ("empty.jpg", b"", "image/jpeg")},
        data={"scenario_id": "demo"},
    )
    assert resp.status_code == 400
