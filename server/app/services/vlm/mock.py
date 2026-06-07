"""Mock provider — 키 없이 plumbing 을 완전 구동하는 결정론적 구현.

⚠️ 정직성(run-cycle 규칙 8): mock 판정은 **이미지 내용을 보지 않는다.**
바이트 해시로 OK/NG 를 결정론적으로 가른다. mock 기반 E2E 통과는
"파이프라인이 흐른다"는 증거일 뿐, "VLM 코어가 옳게 판정한다"는 증거가 아니다.
실제 판정 검증은 M0.1(실측, 키 필요)에서만 가능하다.
"""

import hashlib

from app.models.inspection import InspectionResult, ScenarioContext, Verdict
from app.services.vlm.base import VLMProvider

_NG_FINDINGS = "모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님)."


class MockProvider(VLMProvider):
    name = "mock"

    async def inspect(
        self, image: bytes, scenario: ScenarioContext
    ) -> InspectionResult:
        digest = hashlib.sha256(image).digest()
        # 결정론적 분기: 동일 이미지는 항상 동일 결과(테스트 재현성).
        is_ng = digest[0] % 3 == 0  # 약 1/3 확률로 NG
        # confidence: 해시 바이트를 0.70~0.99 로 사상.
        confidence = round(0.70 + (digest[1] / 255) * 0.29, 2)
        return InspectionResult(
            verdict=Verdict.NG if is_ng else Verdict.OK,
            findings=_NG_FINDINGS if is_ng else "",
            confidence=confidence,
        )
