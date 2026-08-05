using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 관리자 PIN 상태(<c>GET /admin/status</c>, 무인증) — 클라이언트가 최초 설정 화면 노출 여부를 결정한다.
/// </summary>
public sealed record AdminStatusResponse
{
    /// <summary>PIN 이 어떤 경로로든(config 또는 파일) 설정됨.</summary>
    [JsonPropertyName("pin_configured")] public required bool PinConfigured { get; init; }
    /// <summary>부트스트랩 활성 && 아직 미설정 — 클라이언트가 최초 설정 화면을 띄워야 함.</summary>
    [JsonPropertyName("setup_required")] public required bool SetupRequired { get; init; }
}

/// <summary><c>POST /admin/setup</c> 요청 본문(wire) — 서버 데이터 폴더의 토큰 + 새 PIN.</summary>
public sealed record AdminSetupInput
{
    [JsonPropertyName("setup_token")] public string? SetupToken { get; init; }
    [JsonPropertyName("new_pin")] public string? NewPin { get; init; }
}

/// <summary>
/// 관리 엔드포인트가 PIN 미설정으로 503 을 낼 때의 본문(wire) — 클라이언트가 RFC problem 원문 대신
/// 전용 최초 설정 화면으로 전환하도록 <c>setup_required</c> 마커를 실어 보낸다(inspect 의
/// <see cref="DetectionUnavailableResponse"/> 패턴과 동형).
/// </summary>
public sealed record AdminSetupRequiredResponse
{
    [JsonPropertyName("setup_required")] public required bool SetupRequired { get; init; }
    [JsonPropertyName("detail")] public required string Detail { get; init; }
}
