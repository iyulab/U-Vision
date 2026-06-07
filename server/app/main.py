"""U-Vision API 진입점.

단일 조직 셀프호스트 전제 — 멀티테넌시 없음.
Cycle 1: health 만. inspect/scenarios 는 후속 사이클에서 라우터로 추가.
"""

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.api.inspect import router as inspect_router
from app.core.config import settings

app = FastAPI(title="U-Vision API", version="0.1.0")

# 단일 조직 셀프호스트 — CORS 는 관대하게 두고 배포 시 reverse proxy 에서 조인다.
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(inspect_router)


@app.get("/api/health")
def health() -> dict[str, str]:
    return {"status": "ok", "provider": settings.vlm_provider}
