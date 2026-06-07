namespace UVision.Api.Configuration;

/// <summary>
/// VLM 및 업로드 설정 — 환경변수에서 바인딩한다.
/// (원본: server/app/core/config.py — VLM_PROVIDER/VLM_MODEL/VLM_API_KEY/MAX_UPLOAD_SIZE_MB)
/// </summary>
public sealed class VlmOptions
{
    /// <summary>mock | openai | google | gpustack | vllm</summary>
    public string Provider { get; set; } = "mock";

    public string Model { get; set; } = "gpt-4o";

    public string ApiKey { get; set; } = "";

    /// <summary>
    /// 셀프호스트/OpenAI 호환 provider(gpustack 등)의 서버 base URL — 경로 없이(예: http://host:8080).
    /// 클라우드 provider(openai/google)는 사용하지 않는다.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>업로드 크기 상한(MB) — DoS 방지.</summary>
    public int MaxUploadSizeMb { get; set; } = 10;
}
