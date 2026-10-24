using System.Text.RegularExpressions;

namespace UVision.Api.Models;

/// <summary>
/// 캡처의 출처 — 장치가 현장에서 찍은 것이 아니라 시험 데이터셋에서 넣은 판정임을 표시한다. 표시가 없으면 현장 촬영이다.
/// 주입한 판정도 판정 · 기록 · 학습 · 기억은 그대로 지나간다(전체 사이클을 시험하는 것이 목적) — 출처는 그 기록이 현장
/// 데이터인 척 섞이지 않게 레코드 · 메트릭 · 장부 · 판정 사건에 남는다.
/// </summary>
public sealed record CaptureOrigin
{
    /// <summary>지금 받는 출처 — 시험 데이터셋 주입.</summary>
    public const string Dataset = "dataset";

    /// <summary>출처 종류(<see cref="Dataset"/>).</summary>
    public required string Kind { get; init; }

    /// <summary>출처 안의 표본 식별(예: 표본 목록 이름과 내용 해시) — 결과를 데이터셋 정답과 잇는 키(선택).</summary>
    public string? Ref { get; init; }

    /// <summary>
    /// 경계 검증 — 종류는 지어진 값만, 식별은 영문 · 숫자 · <c>. _ : -</c> 128자 이하만 받는다(변형하지 않는다). 식별만 있고
    /// 종류가 없으면 거부한다. 위반이면 사유, 아니면 null.
    /// </summary>
    public static string? Validate(string? kind, string? reference) =>
        kind is null ? (reference is null ? null : "origin_ref 는 origin 과 함께만")
        : kind != Dataset ? $"origin 은 '{Dataset}' 만 받는다"
        : reference is not null && !RefPattern.IsMatch(reference) ? "origin_ref 는 영문 · 숫자 · . _ : - 로 된 128자 이하"
        : null;

    private static readonly Regex RefPattern = new(@"^[A-Za-z0-9._:-]{1,128}\z", RegexOptions.Compiled);
}
