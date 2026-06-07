namespace UVision.Api.Configuration;

/// <summary>
/// VLM 및 업로드 설정 — 환경변수에서 바인딩한다.
/// (원본: server/app/core/config.py — VLM_PROVIDER/VLM_MODEL/VLM_API_KEY/MAX_UPLOAD_SIZE_MB)
/// </summary>
public sealed class VlmOptions
{
    /// <summary>mock | openai | google | vllm</summary>
    public string Provider { get; set; } = "mock";

    public string Model { get; set; } = "gpt-4o";

    public string ApiKey { get; set; } = "";

    /// <summary>업로드 크기 상한(MB) — DoS 방지.</summary>
    public int MaxUploadSizeMb { get; set; } = 10;
}
