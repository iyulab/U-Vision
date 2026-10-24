namespace UVision.Api.Configuration;

/// <summary>
/// 전용 ML 비전 분류기 설정(신뢰성 플라이휠 ②~③). 호스트 config 섹션 <c>UVision:Ml</c>.
/// <para>
/// 기본 <c>none</c> = ML 모델 없음(플라이휠 ① 단계 — VLM만). 데이터가 쌓여 전용 모델을
/// 학습하면 <c>mloop</c> 로 전환한다. 분류기는 <see cref="VlmOptions"/> 와 직교한다.
/// </para>
/// </summary>
public sealed class MlOptions
{
    /// <summary>none | mock | mloop</summary>
    public string Provider { get; set; } = "none";

    /// <summary>mloop serve 의 base URL(예: http://host:5000). mloop provider 전용 — 이미지를 내용으로 보내므로 mloop 0.45.0+.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>학습된 모델 이름(mloop promote 시 지정한 이름). 기본 "default".</summary>
    public string Model { get; set; } = "default";

    /// <summary>mloop serve JWT Bearer 토큰(/predict 는 인증 필요). 비어 있으면 헤더 미부착.</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// 2중체크(③) 검토 임계값. VLM 또는 ML 신뢰도가 이 값 미만이면 <c>requires_review</c>.
    /// 0(기본)이면 신뢰도 게이팅 비활성 — VLM·ML 불일치만 검토를 유발한다.
    /// 시나리오 무관 서버 레버(샘플 정조준 아님). 0.0~1.0.
    /// </summary>
    public double ReviewConfidenceThreshold { get; set; } = 0.0;
}
