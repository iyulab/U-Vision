using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 계수 판정의 근거 — 검출기가 낸 상자를 신뢰도 문턱으로 걸러 센 결과. 결과 v2 와 판정 레코드가 같은 모양으로 싣는다.
/// 계수 시나리오의 판정에만 있고 그 밖의 응답 · 레코드에는 나타나지 않는다(byte 보존).
/// </summary>
public sealed record CountInfo
{
    /// <summary>문턱을 넘고 세는 종류에 드는 상자 수.</summary>
    [JsonPropertyName("total")] public required int Total { get; init; }

    /// <summary>종류(검출기 라벨)별 개수.</summary>
    [JsonPropertyName("by_class")] public required IReadOnlyDictionary<string, int> ByClass { get; init; }

    /// <summary>시나리오의 기대 개수 — 구성 확인(<see cref="Items"/> 가 있는 판정)이면 종류별 기대의 합계.</summary>
    [JsonPropertyName("expected")] public required int Expected { get; init; }

    /// <summary>시나리오의 허용 오차(0 = 정확히). 구성 확인에는 허용 오차가 없어 언제나 0.</summary>
    [JsonPropertyName("tolerance")] public required int Tolerance { get; init; }

    /// <summary>
    /// 종류별 기대 대비 항목 목록 — 구성 확인 시나리오(<c>expected_by_class</c>)의 판정에만 있다. 모자라는 종류가 하나라도 있으면
    /// 판정은 NG 다. 총 개수만 견주는 계수의 응답 · 레코드에는 나타나지 않는다(byte 보존).
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CountItemInfo>? Items { get; init; }

    /// <summary>
    /// 기대보다 많거나 기대에 없는 종류를 어떻게 봤는가 — <c>ng</c> · <c>warn</c> · <c>ignore</c>. 구성 확인에만 있다
    /// (과잉이 판정에 반영됐는지 읽는 이가 시나리오를 다시 열지 않아도 되게).
    /// </summary>
    [JsonPropertyName("extra_policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtraPolicy { get; init; }

    /// <summary>센 데 쓴 신뢰도 문턱 — 시나리오 값, 없으면 설치 기본.</summary>
    [JsonPropertyName("min_confidence")] public required double MinConfidence { get; init; }

    /// <summary>
    /// 문턱 근처의 상자를 넣고 빼는 데 따라 결론이 달라진다 — 그러면 판정은 NG 이고 사람 확인을 요구한다.
    /// </summary>
    [JsonPropertyName("uncertain")] public required bool Uncertain { get; init; }

    /// <summary>센 상자 — 근거로 보인다.</summary>
    [JsonPropertyName("boxes")] public required IReadOnlyList<CountBox> Boxes { get; init; }

    /// <summary>
    /// 세지 않았지만 문턱 바로 아래(0.1 안)인 상자 — 있으면 넣고 빼는 데 따라 판정이 바뀔 수 있다. 없으면 생략한다.
    /// </summary>
    [JsonPropertyName("near_boxes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CountBox>? NearBoxes { get; init; }

    /// <summary>상자를 낸 검출 모델 파일의 SHA-256 — 서버가 알려 줄 때만. 이 개수가 어느 모델의 것인지 남기는 출처.</summary>
    [JsonPropertyName("model_sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelSha256 { get; init; }
}

/// <summary>구성 항목 하나 — 한 종류의 기대와 센 수, 그리고 둘을 견준 상태(<c>ok</c> · <c>missing</c> · <c>extra</c>).</summary>
public sealed record CountItemInfo
{
    [JsonPropertyName("class")] public required string Class { get; init; }

    [JsonPropertyName("expected")] public required int Expected { get; init; }

    [JsonPropertyName("found")] public required int Found { get; init; }

    /// <summary><c>ok</c> 기대와 같음(또는 무시하기로 한 과잉) · <c>missing</c> 모자람 · <c>extra</c> 많거나 기대에 없는 종류.</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }
}

/// <summary>센 상자 하나 — 좌표는 원본 이미지 기준 비율(0~1, 왼쪽 위 + 크기)이다.</summary>
public sealed record CountBox
{
    [JsonPropertyName("class")] public required string Class { get; init; }

    [JsonPropertyName("x")] public required double X { get; init; }

    [JsonPropertyName("y")] public required double Y { get; init; }

    [JsonPropertyName("w")] public required double W { get; init; }

    [JsonPropertyName("h")] public required double H { get; init; }

    [JsonPropertyName("confidence")] public required double Confidence { get; init; }
}
