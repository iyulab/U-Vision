namespace UVision.Api.Configuration;

/// <summary>
/// 판정 사건 내보내기 — config 섹션 <c>UVision:Events</c>. 웹훅이 하나도 없으면(기본) 사건을 쓰지도 보내지도
/// 않는다(부수효과 0).
/// </summary>
public sealed class EventsOptions
{
    public List<WebhookOptions> Webhooks { get; set; } = [];

    /// <summary>보내기 주기(초) — 새 사건은 이 주기를 기다리지 않고 바로 보낸다.</summary>
    public int DeliveryIntervalSeconds { get; set; } = 15;

    /// <summary>이 횟수를 실패하면 보내기를 멈추고 <c>dead/</c> 로 옮긴다(재시도 간격은 지수로 늘어 최대 1시간).</summary>
    public int MaxAttempts { get; set; } = 15;
}

/// <summary>웹훅 받는 곳 하나.</summary>
public sealed class WebhookOptions
{
    /// <summary>받는 곳 이름 — outbox 디렉터리가 된다(식별자 문자만).</summary>
    public string Name { get; set; } = "";

    /// <summary>POST 할 주소(http · https).</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// 서명 비밀(Standard Webhooks) — <c>whsec_</c> 로 시작하면 그 뒤를 base64 로 읽고, 아니면 UTF-8 바이트.
    /// 비우면 서명 헤더를 붙이지 않는다.
    /// </summary>
    public string Secret { get; set; } = "";
}
