"""VLM provider 팩토리.

설정의 vlm_provider 값으로 구체 provider 를 선택한다.
앱 코드는 get_provider() 만 호출하고 구현체를 모른다.
"""

from app.core.config import Settings
from app.services.vlm.base import VLMProvider
from app.services.vlm.mock import MockProvider


def get_provider(settings: Settings) -> VLMProvider:
    provider = settings.vlm_provider.lower()

    if provider == "mock":
        return MockProvider()

    if provider == "openai":
        from app.services.vlm.openai_provider import OpenAIProvider

        return OpenAIProvider(settings.vlm_api_key, settings.vlm_model)

    if provider == "google":
        from app.services.vlm.google_provider import GoogleProvider

        return GoogleProvider(settings.vlm_api_key, settings.vlm_model)

    if provider == "vllm":
        # P5(엣지 추론)에서 구현. 인터페이스만 예약.
        raise NotImplementedError("vLLM 어댑터는 P5(엣지 추론 모드)에서 구현 예정")

    raise ValueError(f"알 수 없는 VLM provider: {settings.vlm_provider}")


__all__ = ["get_provider", "VLMProvider"]
