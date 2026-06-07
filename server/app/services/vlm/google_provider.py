"""Google (Gemini Vision) 어댑터.

⚠️ UNVERIFIED — API 키 부재로 미실행. SDK(`google-genai`) 형태는 2026-01 기준
지식 기반이며, 실제 동작은 M0.1(C3 벤치마크 하네스, 키 필요)에서 검증해야 한다.

SDK 는 lazy import — 미설치여도 mock 경로/모듈 로드에 영향 없음.
"""

from app.models.inspection import InspectionResult, ScenarioContext
from app.services.vlm.base import VLMProvider
from app.services.vlm.prompt import build_system_prompt


class GoogleProvider(VLMProvider):
    name = "google"

    def __init__(self, api_key: str, model: str) -> None:
        self._api_key = api_key
        self._model = model

    async def inspect(
        self, image: bytes, scenario: ScenarioContext
    ) -> InspectionResult:
        from google import genai  # lazy: 미검증 의존성 격리
        from google.genai import types

        client = genai.Client(api_key=self._api_key)
        resp = await client.aio.models.generate_content(
            model=self._model,
            contents=[
                types.Part.from_bytes(data=image, mime_type="image/jpeg"),
                "이 제품 이미지를 판정하라.",
            ],
            config=types.GenerateContentConfig(
                system_instruction=build_system_prompt(scenario),
                response_mime_type="application/json",
                response_schema=InspectionResult,
            ),
        )
        return InspectionResult.model_validate_json(resp.text or "{}")
