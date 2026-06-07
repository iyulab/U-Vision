"""검사 엔드포인트 — 캡처 이미지를 VLM 으로 판정한다.

C2: 시나리오는 하드코딩 1개("demo"). 시나리오 저장소 기반 조회는
P2(시나리오 관리, ~C8)에서 대체한다. 여기서 선제 구현하지 않는다.
"""

import uuid
from datetime import datetime, timezone

from fastapi import APIRouter, File, Form, HTTPException, UploadFile

from app.core.config import settings
from app.models.inspection import InspectResponse, ScenarioContext
from app.services.vlm import get_provider

router = APIRouter(prefix="/api", tags=["inspect"])

_ALLOWED_TYPES = {"image/jpeg", "image/png"}

# C2 임시 시나리오 카탈로그 — P2 에서 저장소로 대체.
_SCENARIOS: dict[str, ScenarioContext] = {
    "demo": ScenarioContext(
        scenario_id="demo",
        criteria=(
            "제품 표면에 긁힘, 이물질, 균열, 솔더 브릿지 등 외관 결함이 없어야 한다. "
            "결함이 보이면 NG, 깨끗하면 OK."
        ),
    ),
}


@router.post("/inspect", response_model=InspectResponse)
async def inspect(
    image: UploadFile = File(...),
    scenario_id: str = Form(...),
) -> InspectResponse:
    if image.content_type not in _ALLOWED_TYPES:
        raise HTTPException(
            status_code=415,
            detail=f"지원하지 않는 이미지 형식: {image.content_type}",
        )

    scenario = _SCENARIOS.get(scenario_id)
    if scenario is None:
        raise HTTPException(status_code=404, detail=f"시나리오 없음: {scenario_id}")

    data = await image.read()
    max_bytes = settings.max_upload_size_mb * 1024 * 1024
    if len(data) > max_bytes:
        raise HTTPException(
            status_code=413,
            detail=f"이미지가 너무 큼(>{settings.max_upload_size_mb}MB)",
        )
    if not data:
        raise HTTPException(status_code=400, detail="빈 이미지")

    provider = get_provider(settings)
    result = await provider.inspect(data, scenario)

    return InspectResponse(
        verdict=result.verdict,
        findings=result.findings,
        confidence=result.confidence,
        timestamp=datetime.now(timezone.utc).isoformat(),
        image_id=f"img_{uuid.uuid4().hex[:8]}",
    )
