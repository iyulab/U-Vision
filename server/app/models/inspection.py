"""검사 도메인 모델 — VLM 입출력 계약.

이 스키마는 provider 와 무관한 안정 계약이다. 어떤 VLM 이 뒤에 있든
inspect() 는 InspectionResult 를 반환한다.
"""

from enum import Enum

from pydantic import BaseModel, Field


class Verdict(str, Enum):
    OK = "OK"
    NG = "NG"


class ScenarioContext(BaseModel):
    """판정에 필요한 시나리오 컨텍스트.

    C2 단계에서는 criteria(자연어 기준)만 사용한다.
    기준 이미지(ok/ng)·ROI 등은 시나리오 관리(P2, ~C8)에서 확장한다.
    여기서 선제 확장하지 않는다(YAGNI).
    """

    scenario_id: str
    criteria: str = ""


class InspectionResult(BaseModel):
    """VLM 판정 결과 — provider 가 반환하는 도메인 객체."""

    verdict: Verdict
    findings: str = Field(default="", description="NG 시 불량 소견 텍스트")
    confidence: float = Field(ge=0.0, le=1.0)


class InspectResponse(BaseModel):
    """`POST /api/inspect` 응답 — 도메인 결과 + API 메타데이터.

    도메인(InspectionResult)과 분리: API 는 timestamp/image_id 를 덧붙인다.
    """

    verdict: Verdict
    findings: str
    confidence: float
    timestamp: str
    image_id: str
