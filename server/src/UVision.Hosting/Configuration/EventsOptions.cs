namespace UVision.Api.Configuration;

/// <summary>
/// 판정 사건 내보내기 — config 섹션 <c>UVision:Events</c>. 받는 곳(웹훅 · MQTT)이 하나도 없으면(기본) 사건을
/// 쓰지도 보내지도 않는다(부수효과 0). 받는 곳 이름은 웹훅 · MQTT 를 통틀어 겹치지 않는다(outbox 디렉터리 이름).
/// </summary>
public sealed class EventsOptions
{
    public List<WebhookOptions> Webhooks { get; set; } = [];

    /// <summary>MQTT 브로커 — 같은 사건(CloudEvents 구조적 모드)을 주제로 낸다.</summary>
    public List<MqttOptions> Mqtt { get; set; } = [];

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

/// <summary>MQTT 브로커 하나 — 사건이 <c>{TopicPrefix}/{시나리오}</c> 로 나간다(MQTT 5, 구조적 CloudEvents).</summary>
public sealed class MqttOptions
{
    /// <summary>받는 곳 이름 — outbox 디렉터리가 된다(식별자 문자만).</summary>
    public string Name { get; set; } = "";

    /// <summary>브로커 호스트 이름 또는 주소.</summary>
    public string Host { get; set; } = "";

    /// <summary>포트 — 0(기본)이면 TLS 여부에 따라 1883 / 8883.</summary>
    public int Port { get; set; }

    /// <summary>TLS 로 잇나(기본 아니오 — 시설 안 브로커). 켜면 서버 인증서를 시스템 신뢰 저장소로 검증한다.</summary>
    public bool UseTls { get; set; }

    /// <summary>사용자 이름 — 비우면 익명.</summary>
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>클라이언트 id — 비우면 <c>u-vision-{Name}</c>. 같은 브로커에 같은 id 로 둘이 잇지 않는다.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>주제 접두 — 사건은 <c>{TopicPrefix}/{scenarioId}</c> 로 나간다(와일드카드 · <c>$</c> 시작 없음).</summary>
    public string TopicPrefix { get; set; } = "u-vision/events";

    /// <summary>서비스 품질 — 1(최소 한 번, 기본) 또는 2(정확히 한 번). 0 은 outbox 의 최소 한 번과 맞지 않아 받지 않는다.</summary>
    public int QualityOfService { get; set; } = 1;
}
