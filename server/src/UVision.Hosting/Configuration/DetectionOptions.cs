namespace UVision.Api.Configuration;

/// <summary>
/// 객체 검출기 설정(계수 시나리오의 검출기 단). 호스트 config 섹션 <c>UVision:Detection</c>.
/// <para>
/// 기본 <c>none</c> = 검출기 없음 — 계수 시나리오는 판정 불가(fail-closed)가 되고 다른 시나리오는 영향이 없다.
/// 검출기는 <see cref="VlmOptions"/> · <see cref="MlOptions"/> 와 직교한다.
/// </para>
/// </summary>
public sealed class DetectionOptions
{
    /// <summary>none | mock | mloop</summary>
    public string Provider { get; set; } = "none";

    /// <summary>mloop serve 의 base URL(예: http://host:5000). mloop provider 전용 — 이미지를 내용으로 보내므로 mloop 0.45.0+.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>검출 모델 이름(mloop promote 시 지정한 이름). 기본 "default".</summary>
    public string Model { get; set; } = "default";

    /// <summary>mloop serve JWT Bearer 토큰(/predict 는 인증 필요). 비어 있으면 헤더 미부착.</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// mloop serve 한 번 호출의 시간 제한(초, 1~600). 검출기는 계수 판정의 유일한 근거라 닿지 않으면 판정 불가이므로,
    /// 멈춘 MLoop 이 판정을 붙잡는 상한이다. 기본 30 — 검출은 분류보다 무거워 분류기 기본(15)보다 넉넉히 둔다(아직 실측 전).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
