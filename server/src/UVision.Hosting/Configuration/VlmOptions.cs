namespace UVision.Api.Configuration;

/// <summary>
/// VLM 및 업로드 설정 — 환경변수에서 바인딩한다.
/// (원본: server/app/core/config.py — VLM_PROVIDER/VLM_MODEL/VLM_API_KEY/MAX_UPLOAD_SIZE_MB)
/// </summary>
public sealed class VlmOptions
{
    /// <summary>mock | openai-compatible | openai | google(<see cref="Services.Vlm.ProviderNames"/>)</summary>
    public string Provider { get; set; } = "mock";

    public string Model { get; set; } = "gpt-4o";

    public string ApiKey { get; set; } = "";

    /// <summary>
    /// openai-compatible 의 서버 주소(예: http://host:8080) — ironhive 가 표준 경로 <c>/v1</c> 를 붙인다(이미 있으면
    /// 그대로). 클라우드 provider(openai/google)는 사용하지 않는다.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>업로드 크기 상한(MB) — DoS 방지.</summary>
    public int MaxUploadSizeMb { get; set; } = 10;
}
