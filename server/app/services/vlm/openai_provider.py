"""OpenAI (GPT-4o Vision) 어댑터.

⚠️ UNVERIFIED — API 키 부재로 미실행. SDK 형태는 2026-01 기준 지식 기반이며,
실제 동작·응답 형태·latency 는 M0.1(C3 벤치마크 하네스, 키 필요)에서 검증해야 한다.
그 전까지 이 코드가 옳다고 가정하지 말 것.

SDK(`openai`)는 lazy import — 미설치여도 mock 경로/모듈 로드에 영향 없음.
"""

import base64

from app.models.inspection import InspectionResult, ScenarioContext
from app.services.vlm.base import VLMProvider
from app.services.vlm.prompt import RESULT_JSON_SCHEMA, build_system_prompt


class OpenAIProvider(VLMProvider):
    name = "openai"

    def __init__(self, api_key: str, model: str) -> None:
        self._api_key = api_key
        self._model = model

    async def inspect(
        self, image: bytes, scenario: ScenarioContext
    ) -> InspectionResult:
        from openai import AsyncOpenAI  # lazy: 미검증 의존성 격리

        client = AsyncOpenAI(api_key=self._api_key)
        data_uri = "data:image/jpeg;base64," + base64.b64encode(image).decode()
        resp = await client.chat.completions.create(
            model=self._model,
            messages=[
                {"role": "system", "content": build_system_prompt(scenario)},
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": "이 제품 이미지를 판정하라."},
                        {"type": "image_url", "image_url": {"url": data_uri}},
                    ],
                },
            ],
            response_format={"type": "json_schema", "json_schema": RESULT_JSON_SCHEMA},
        )
        content = resp.choices[0].message.content or "{}"
        return InspectionResult.model_validate_json(content)
