namespace UVision.Api.Configuration;

/// <summary>
/// 문제 큐 설정 — config 섹션 <c>UVision:Questions</c>. 사람이 한 번에 한 사진씩 «이 사진은 무엇인가» 를 푸는 학습용 출제의 규칙이다.
/// 기본은 운영 판정자가 가장 헷갈리는 사진을 고르는 규칙(<see cref="UncertainRule"/>)이고, 신호가 아직 없으면 무작위로 시작한다.
/// </summary>
public sealed class QuestionsOptions
{
    /// <summary>모든 사진에서 균등하게 고른다. 기억 단이 꺼져 있어도 같다.</summary>
    public const string RandomRule = "random";

    /// <summary>
    /// 켜진 판정자 중 가장 강한 것이 가장 헷갈린 사진을 고른다(걸음마다 <see cref="RandomFloor"/> 의 확률로 무작위): 패치 점수가 있으면
    /// 패치 판정자(문턱은 대표 표본의 정상 라벨로만), 없으면 전역 기억 단. 신호가 없으면 무작위로 시작한다.
    /// </summary>
    public const string UncertainRule = "uncertain";

    /// <summary><c>uncertain</c>(기본) | <c>random</c>.</summary>
    public string Rule { get; set; } = UncertainRule;

    /// <summary>
    /// <c>uncertain</c> 규칙에서 걸음 중 무작위로 낼 몫(0 ~ 1, 기본 0.25). 어려운 사진만 내면 풀이가 거기 몰리고, 판정의 합격선을
    /// 잡을 대표 표본이 모이지 않는다. <c>random</c> 규칙에서는 쓰지 않는다.
    /// </summary>
    public double RandomFloor { get; set; } = 0.25;

    /// <summary>문제 후보로 삼는 판정의 최근 일수(오늘 포함, 1~60, 기본 7). 이보다 오래된 판정은 문제로 내지 않는다.</summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>잘못된 값은 기동에서 멈춘다 — 첫 문제 요청이 아니라.</summary>
    public void Validate()
    {
        if (Rule is not (RandomRule or UncertainRule))
            throw new ArgumentException($"UVision:Questions:Rule 은 '{RandomRule}' 또는 '{UncertainRule}' 이어야 한다: '{Rule}'");
        if (double.IsNaN(RandomFloor) || RandomFloor is < 0 or > 1)
            throw new ArgumentException($"UVision:Questions:RandomFloor 는 0 ~ 1 이어야 한다: {RandomFloor}");
        if (WindowDays is < 1 or > 60)
            throw new ArgumentException($"UVision:Questions:WindowDays 는 1 ~ 60 이어야 한다: {WindowDays}");
    }
}
