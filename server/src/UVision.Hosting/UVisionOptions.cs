using UVision.Api.Configuration;

namespace UVision.Api;

/// <summary>
/// U-Vision 임베드 설정 — 호스트 config 섹션 "UVision" 에서 바인딩한다.
/// 임베드되면 프로세스 환경변수를 직접 읽지 못하므로(호스트와 충돌), 모든 설정이 이 한 곳으로 모인다.
/// </summary>
public sealed class UVisionOptions
{
    public const string SectionName = "UVision";

    public VlmOptions Vlm { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();

    /// <summary>관리자 PIN(미설정 시 관리 엔드포인트 503, 운영은 정상).</summary>
    public string? AdminPin { get; set; }

    /// <summary>
    /// PWA가 마운트되는 URL 경로. 기본 "/u-vision".
    /// ⚠️ 임베드된 클라이언트 빌드의 Vite base(VITE_BASE)와 반드시 일치해야 한다 — SW/manifest/번들은
    /// 빌드타임 base를 baked하므로, 다른 경로로 마운트하려면 클라이언트를 그 VITE_BASE로 재빌드해야 한다
    /// (미들웨어는 index.html만 best-effort 치환). 기본값 사용을 권장.
    /// </summary>
    public string BasePath { get; set; } = "/u-vision";

    /// <summary>API 네임스페이스. 기본 "/api/u-vision".</summary>
    public string ApiBasePath { get; set; } = "/api/u-vision";

    /// <summary>웹 뷰어 제목(런타임 config로 SPA에 주입). 기본 "U-Vision".</summary>
    public string Title { get; set; } = "U-Vision";
}
