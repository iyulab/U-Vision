namespace UVision.Api.Configuration;

/// <summary>오라클(④-B) 설정 — config 섹션 UVision:Oracle. 기본 none = 비활성(스윕 미동작).</summary>
public sealed class OracleOptions
{
    /// <summary>none | openai-compatible(셀프호스트). cloud 오라클은 없다 — 그 밖 값은 기동에서 거부.</summary>
    public string Provider { get; set; } = "none";
    public string Endpoint { get; set; } = "";        // OpenAI 호환 서버 주소
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";           // 더 센 모델(주 VLM 보다 큰)
    public int SweepIntervalSeconds { get; set; } = 60;
    public int BatchCap { get; set; } = 10;            // 틱당 전역 처리 상한(E3 비용)
    public int LookbackDays { get; set; } = 2;         // 스캔 날짜 범위(오늘 포함)
}
