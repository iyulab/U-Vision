namespace UVision.Api.Configuration;

/// <summary>
/// 확정 사례 임베딩 기억 단 설정(system 1). 호스트 config 섹션 <c>UVision:Memory</c>.
/// <para>
/// 기본 <c>none</c> = 단 없음(지금 동작). 임베딩은 MLoop <c>mloop serve</c> 의 <c>POST /embed</c> 가 만들고, 쓸
/// 인코더는 설치 쪽이 MLoop 에 넣는다 — U-Vision 은 그 id 만 안다. 전용 ML(<see cref="MlOptions"/>)과 분리돼 있어 ML
/// 없이도 켤 수 있다.
/// </para>
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>none | mock | mloop</summary>
    public string Provider { get; set; } = "none";

    /// <summary>mloop serve 의 base URL(예: http://host:5000). mloop provider 전용.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>MLoop 에 설치된 인코더 id(<c>mloop encoder list</c>). mloop provider 전용.</summary>
    public string Encoder { get; set; } = "";

    /// <summary>mloop serve JWT Bearer 토큰(<c>/embed</c> 는 인증 필요). 비어 있으면 헤더 미부착.</summary>
    public string Token { get; set; } = "";

    /// <summary>답하려면 모두 같은 클래스여야 하는 최근접 이웃 수. 기억이 이보다 적으면 기권한다.</summary>
    public int Neighbors { get; set; } = 5;

    /// <summary>
    /// 최근접 이웃의 코사인 거리 상한(0~2). 이보다 멀면 «본 적 없는 이미지»로 보고 기권한다. 초기값이다 — 장부에 남는
    /// 거리로 재생해 다시 고른다.
    /// </summary>
    public double MaxDistance { get; set; } = 0.15;
}
