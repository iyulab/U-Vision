using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>기준 이미지 분류 — OK 기준 / NG 기준. 닫힌 2값 집합(경로 <c>references/{ok|ng}/</c>).</summary>
public enum ReferenceLabel
{
    Ok,
    Ng,
}

/// <summary>
/// 기준 이미지 메타데이터(목록 응답). 바이트는 별도 서빙 엔드포인트로 가져온다.
/// </summary>
public sealed record ReferenceInfo
{
    [JsonPropertyName("ref_id")] public required string RefId { get; init; }

    [JsonPropertyName("label")] public required ReferenceLabel Label { get; init; }

    /// <summary>NG 기준 이미지의 불량 유형명(scenario.json.ng_labels). OK 면 null.</summary>
    [JsonPropertyName("ng_label")] public string? NgLabel { get; init; }

    /// <summary>
    /// 선별(<c>classification</c>) 기준 이미지의 클래스 id — 선별 시나리오에서만 채워진다(검사의 응답은 그대로). 그 경우
    /// <see cref="Label"/> 은 클래스의 OK/NG 보기(꼭 잡을 클래스면 ng)다.
    /// </summary>
    [JsonPropertyName("class")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Class { get; init; }
}

/// <summary>선별 기준 이미지 한 장의 저장 위치 — 클래스 폴더 이름(소문자)과 기준 이미지 id.</summary>
public sealed record ClassReference(string ClassDir, string RefId);

/// <summary>
/// few-shot 판정에 결합할 기준 이미지(바이트 포함). provider 가 비전 입력으로 변환한다.
/// 도메인 타입이므로 provider 라이브러리(ironhive) 타입을 노출하지 않는다 —
/// <see cref="IsPng"/> 로 형식만 전달하고 변환은 provider 가 한다.
/// </summary>
public sealed record ReferenceImage
{
    public required ReadOnlyMemory<byte> Data { get; init; }

    public required ReferenceLabel Label { get; init; }

    public string? NgLabel { get; init; }

    /// <summary>선별(<c>classification</c>) 기준 이미지의 클래스 id — 있으면 프롬프트 표지가 OK/NG 가 아니라 이 클래스로 붙는다.</summary>
    public string? ClassId { get; init; }

    public bool IsPng { get; init; }
}
