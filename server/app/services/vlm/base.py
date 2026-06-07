"""VLMProvider 추상 인터페이스.

앱 코드는 이 인터페이스만 안다. 구체 provider(mock/openai/google/vllm)는
팩토리(__init__.get_provider)가 설정에 따라 주입한다.
"""

from abc import ABC, abstractmethod

from app.models.inspection import InspectionResult, ScenarioContext


class VLMProvider(ABC):
    """비전 판정 provider 경계."""

    name: str

    @abstractmethod
    async def inspect(
        self, image: bytes, scenario: ScenarioContext
    ) -> InspectionResult:
        """이미지 1장을 시나리오 컨텍스트로 판정한다."""
        raise NotImplementedError
