using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 한 검사의 이상 지도 — 질의 이미지의 패치마다 정상 기억 패치 전부까지의 최근접 코사인 거리. 클수록 그 자리가 정상 기억과
/// 멀다. 판정에는 쓰이지 않고(관찰 전용) 검토 화면이 원본 위에 열지도로 그리는 근거다.
/// </summary>
public sealed record AnomalyMap
{
    /// <summary>점수를 낸 인코더의 판 — 다른 판의 지도끼리는 견줄 수 없다.</summary>
    [JsonPropertyName("encoder")] public required string Encoder { get; init; }

    [JsonPropertyName("rows")] public required int Rows { get; init; }

    [JsonPropertyName("columns")] public required int Columns { get; init; }

    /// <summary>격자가 덮은 영역(원본 이미지 픽셀). 패치 (r, c) 는 이 영역을 rows × columns 등분한 r 번째 행 · c 번째 열이다. 모르면 생략.</summary>
    [JsonPropertyName("crop")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public AnomalyCrop? Crop { get; init; }

    /// <summary>패치별 거리, 행 우선(<c>rows × columns</c> 개), 소수 넷째 자리까지.</summary>
    [JsonPropertyName("distances")] public required IReadOnlyList<double> Distances { get; init; }

    /// <summary>이미지 점수 = 패치 거리의 최댓값(장부 <c>memory:patch_score</c> 와 같은 값).</summary>
    [JsonPropertyName("patch_score")] public required double PatchScore { get; init; }

    /// <summary>같은 정상 기억에 견준 전역 점수(장부 <c>memory:global_score</c>).</summary>
    [JsonPropertyName("global_score")] public required double GlobalScore { get; init; }

    /// <summary>점수를 낸 정상 기억의 사진 수.</summary>
    [JsonPropertyName("bank_size")] public required int BankSize { get; init; }
}

/// <summary>원본 이미지의 한 영역(픽셀): 왼쪽 · 위 · 오른쪽 · 아래.</summary>
public sealed record AnomalyCrop
{
    [JsonPropertyName("x0")] public required double X0 { get; init; }

    [JsonPropertyName("y0")] public required double Y0 { get; init; }

    [JsonPropertyName("x1")] public required double X1 { get; init; }

    [JsonPropertyName("y1")] public required double Y1 { get; init; }
}
