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

    /// <summary>
    /// 판정 한 건의 VLM 호출 시간 한도(초) — 0 이면 한도 없음(기본, provider 의 전송 설정에 맡긴다), 그 밖은 1~600.
    /// 생각하는(reasoning) 모델은 어떤 장면에서 수십 초~분 단위로 추론하고 SDK 는 실패를 재시도하므로, 작업자가 기다리는
    /// 판정이 게이트웨이 한도까지 매달리지 않게 묶는다. 넘으면 VLM 실패와 같이 fail-closed(판정 불가 503)다.
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// 판정 한 건의 모델 출력 토큰 상한(기본 4096, 256~131072). 생각하는(reasoning) 모델은 답(JSON) 앞에 추론 토큰을 쓰므로, 어려운
    /// 장면에서 추론이 상한을 다 쓰면 답 없이 끝난다(판정 불가 — 로그에 «출력 토큰 한도»로 남는다). 그런 모델은 올린다 — 서버의 문맥 길이를
    /// 넘기면 서버가 요청을 거부하니 그 안에서. 생각하지 않는 모델은 짧게 끝나 영향이 없다.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// 생각하는 모델의 추론 강도 — 비우면(기본) 보내지 않는다(서버 · 모델 기본값). <c>none</c> · <c>minimal</c> · <c>low</c> ·
    /// <c>medium</c> · <c>high</c> · <c>xhigh</c>. 서버가 받지 않으면 효과가 없다 — 켜기 전에 그 서버에서 지연이 줄어드는지 잰다.
    /// </summary>
    public string? ThinkingEffort { get; set; }

    /// <summary>업로드 크기 상한(MB) — DoS 방지.</summary>
    public int MaxUploadSizeMb { get; set; } = 10;
}
