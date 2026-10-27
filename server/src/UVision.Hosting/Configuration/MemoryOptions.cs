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

    /// <summary>
    /// mloop serve 한 번 호출의 시간 제한(초, 1~600). 질의(한 장)와 동기화(최대 64장 묶음)가 같은 클라이언트를 쓴다 —
    /// 넘으면 질의는 기권, 동기화는 다음 주기에 다시 한다. 기본 30 — CPU 에서 인코더 첫 호출 ~0.6초 · 이후 한 장
    /// ~0.1초 실측(2026-10-24)이라 64장 묶음과 느린 장비에 여유를 둔다.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>답하려면 모두 같은 클래스여야 하는 최근접 이웃 수. 기억이 이보다 적으면 기권한다.</summary>
    public int Neighbors { get; set; } = 5;

    /// <summary>
    /// 최근접 이웃의 코사인 거리 상한(0~2). 이보다 멀면 «본 적 없는 이미지»로 보고 기권한다. 초기값이다 — 장부에 남는
    /// 거리로 재생해 다시 고른다.
    /// </summary>
    public double MaxDistance { get; set; } = 0.15;

    /// <summary>
    /// 패치 이상 점수를 장부에 쌓는다(Shadow — 판정에는 쓰이지 않는다). 켜면 정상 확정 사례의 패치 벡터를 기억 옆에 두고,
    /// 검사마다 전역 · 패치 두 점수를 같은 정상 기억에 견주어 장부 annotation 에 남긴다. 어느 쪽을 쓸지는 시나리오마다 쌓인
    /// 점수를 재생해 고른다. 질의가 패치 모드(한 장 ~1 MB 응답)로 바뀌고 점수 계산에 CPU 가 든다 — 켜기 전에 그 장비에서 잰다.
    /// 인코더가 패치 모드를 지원해야 한다(mock · mloop 0.46.0+).
    /// </summary>
    public bool Patches { get; set; }

    /// <summary>
    /// 패치 기억에 둘 정상 사례의 상한(가장 최근 확정부터). 메모리는 사례 수 × 패치 수 × 차원 × 4바이트다 —
    /// dinov2-small(256 패치 · 384차원)에서 200장 ≈ 79 MB, 시나리오마다. 기억이 수백 장이면 패치가 전역 이상이고 수십 장이면
    /// 전역이 안전하다는 재생 결과가 있어 기본 200.
    /// </summary>
    public int PatchBankCap { get; set; } = 200;

    /// <summary>
    /// 기억 동기화 주기(초, 최소 5). 동기화가 새 확정 사례를 임베딩하고 라벨 변경을 반영하므로, 라벨이 바뀐 뒤 기억에
    /// 보이기까지 이만큼 걸린다.
    /// </summary>
    public int SyncIntervalSeconds { get; set; } = 60;
}
