"""VLM 프롬프트 및 구조화 출력 스키마 — provider 간 공유.

판정 일관성을 위해 프롬프트/스키마는 한 곳에서 정의하고 어댑터가 재사용한다.
"""

from app.models.inspection import ScenarioContext

_SYSTEM = (
    "당신은 제조 현장의 비전 검사 판정기다. "
    "주어진 제품 이미지를 시나리오의 판정 기준에 따라 OK 또는 NG 로 판정한다. "
    "NG 이면 불량 소견을 한국어로 구체적으로 기술한다. "
    "경계 사례일수록 confidence 를 낮춘다."
)


def build_system_prompt(scenario: ScenarioContext) -> str:
    criteria = scenario.criteria or "(기준 미지정 — 일반적 외관 결함 기준 적용)"
    return f"{_SYSTEM}\n\n[판정 기준]\n{criteria}"


# OpenAI response_format(json_schema) / 호환 provider 용 스키마.
RESULT_JSON_SCHEMA = {
    "name": "inspection_result",
    "strict": True,
    "schema": {
        "type": "object",
        "properties": {
            "verdict": {"type": "string", "enum": ["OK", "NG"]},
            "findings": {"type": "string"},
            "confidence": {"type": "number"},
        },
        "required": ["verdict", "findings", "confidence"],
        "additionalProperties": False,
    },
}
