"""중앙 설정 — pydantic-settings 로 .env / 환경변수 로드."""

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    # VLM provider (Cycle 2 에서 어댑터가 소비)
    vlm_provider: str = "mock"  # mock | openai | google | vllm
    vlm_model: str = "gpt-4o"
    vlm_api_key: str = ""

    # 업로드 제한
    max_upload_size_mb: int = 10


settings = Settings()
