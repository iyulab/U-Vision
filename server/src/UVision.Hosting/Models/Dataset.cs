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

    /// <summary>적용한 선정 규칙과 뺀 건수 — 같은 규칙으로 다시 만들 수 있게(재현). 규칙 이전의 export 에는 없다.</summary>
    [JsonPropertyName("selection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatasetSelection? Selection { get; init; }
}

/// <summary>데이터셋 선정 기록 — 정답은 사람 확정 라벨, 열린 감사 충돌은 빼고, OK 는 NG 쪽의 배수까지.</summary>
public sealed record DatasetSelection
{
    /// <summary>OK 표본 상한(NG 쪽 건수의 배수).</summary>
    [JsonPropertyName("ok_ratio_cap")] public required double OkRatioCap { get; init; }

    /// <summary>판정원이 엇갈렸거나 검토가 필요했던 건 — 상한 없이 넣었다.</summary>
    [JsonPropertyName("hard_negatives")] public required int HardNegatives { get; init; }

    /// <summary>블라인드 감사 충돌이 풀리지 않아 뺀 라벨.</summary>
    [JsonPropertyName("dropped_conflicted")] public required int DroppedConflicted { get; init; }

    /// <summary>OK 상한을 넘어 뺀 OK.</summary>
    [JsonPropertyName("dropped_ok_over_cap")] public required int DroppedOkOverCap { get; init; }
}

/// <summary>
/// <c>GET {api}/datasets/readiness</c> 응답 — 전용 ML 학습 준비 신호. 빌드 · 재빌드는 관리자가 하고, 이 신호는 «할 때가
/// 됐다»만 알린다. 근거가 없으면 null 로 생략한다(false 로 위장하지 않는다).
/// </summary>
public sealed record TrainingReadinessResponse
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>시나리오 클래스마다 학습에 넣을 수 있는 사람 확정 표본 수(선정 규칙 적용 뒤).</summary>
    [JsonPropertyName("classes")] public required IReadOnlyList<TrainingClassCountDto> Classes { get; init; }

    [JsonPropertyName("min_per_class")] public required int MinPerClass { get; init; }

    /// <summary>쓰는 모델이 없을 때만 — 모든 클래스가 기준에 닿았나.</summary>
    [JsonPropertyName("build_ready")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BuildReady { get; init; }

    [JsonPropertyName("active_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveVersion { get; init; }

    /// <summary>쓰는 모델의 데이터셋 이후 새로 확정된 라벨 — 모델이 어느 데이터셋을 배웠는지 모르면 생략.</summary>
    [JsonPropertyName("new_labels_since_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NewLabelsSinceModel { get; init; }

    [JsonPropertyName("rebuild_min_new_labels")] public required int RebuildMinNewLabels { get; init; }

    [JsonPropertyName("rebuild_suggested")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RebuildSuggested { get; init; }
}

public sealed record TrainingClassCountDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("count")] public required int Count { get; init; }
}
