using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 데이터셋 export 의 단일 항목 — 사람이 라벨한 이미지 1장.
/// 신뢰성 플라이휠 ②(전용 ML 빌드)의 학습 입력 한 행.
/// </summary>
public sealed record DatasetItem
{
    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    /// <summary>원본 결과의 날짜 버킷(yyyy-MM-dd).</summary>
    [JsonPropertyName("date")] public required string Date { get; init; }

    /// <summary>사람 라벨 원본값(예: "OK"/"NG").</summary>
    [JsonPropertyName("label")] public required string Label { get; init; }

    /// <summary>학습 디렉토리명(MLoop class label = 폴더명). 라벨 소문자.</summary>
    [JsonPropertyName("class_dir")] public required string ClassDir { get; init; }

    /// <summary>export 루트 기준 상대 이미지 경로(예: <c>images/ng/img_abc.jpg</c>).</summary>
    [JsonPropertyName("image_file")] public required string ImageFile { get; init; }
}

/// <summary>클래스별 표본 수 집계.</summary>
public sealed record DatasetClassCount
{
    [JsonPropertyName("class_dir")] public required string ClassDir { get; init; }

    [JsonPropertyName("count")] public required int Count { get; init; }
}

/// <summary>
/// 데이터셋 export 결과 manifest — MLoop 이미지분류 학습 입력의 <b>불변 스냅샷</b>.
/// 디스크 레이아웃: <c>{scenario}/datasets/{export_id}/images/{class}/{image_id}.{ext}</c> + 본 manifest.
/// <para>
/// 학습기는 <see cref="ImagesRoot"/>(예: <c>datasets/{export_id}/images</c>)를 가리키면
/// <c>{class}/</c> 하위 폴더명을 클래스 라벨로 자동 인식한다.
/// </para>
/// </summary>
public sealed record DatasetExportManifest
{
    [JsonPropertyName("export_id")] public required string ExportId { get; init; }

    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>export 생성 시각. ISO-8601 UTC.</summary>
    [JsonPropertyName("created_at")] public required string CreatedAt { get; init; }

    /// <summary>학습기가 가리킬 이미지 루트(시나리오 디렉토리 기준 상대경로).</summary>
    [JsonPropertyName("images_root")] public required string ImagesRoot { get; init; }

    [JsonPropertyName("total")] public required int Total { get; init; }

    [JsonPropertyName("classes")] public required IReadOnlyList<DatasetClassCount> Classes { get; init; }

    /// <summary>학습 적합성 경고(단일 클래스·표본 부족·이미지 누락 등). 비어 있으면 양호.</summary>
    [JsonPropertyName("warnings")] public required IReadOnlyList<string> Warnings { get; init; }

    [JsonPropertyName("items")] public required IReadOnlyList<DatasetItem> Items { get; init; }
}
