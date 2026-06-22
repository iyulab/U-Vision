using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 관측성 메트릭의 단위 — inspect 1건의 예측 신호를 담는 jsonl row(신뢰성 플라이휠 B3).
/// <c>{scenario}/metrics/{yyyy-MM-dd}.jsonl</c> 에 append-only 로 쌓여 agreement rate·degrade율·
/// calibration·NG recall 시계열의 입력이 된다(Q2 평가·Q3 재빌드 트리거·A1 권한이양 격상의 데이터 근거).
/// <para>
/// <b>내부 관측 스키마 — wire 가 아니다.</b> <see cref="InspectResponse"/>/<see cref="StoredResult"/> 와
/// 별개의 파일에 산다(메트릭을 wire 로 노출하지 않음 — 수요 미입증·YAGNI). raw confidence 를 그대로
/// 저장한다(표준화 점수는 calibrator 로 재구성 가능한 derived 값이라 캘리브레이션 입력=raw 가 근본).
/// </para>
/// <para>
/// nullable ML 필드 = <b>degrade 표현</b>. 분류 성공이면 ml_* 를 채우고 <see cref="MlDegraded"/>=false,
/// ML enabled 인데 분류 실패면 ml_* 는 null·<see cref="MlDegraded"/>=true(A3 <c>MlOutcome</c> 가 만든
/// "실패↔비활성" 구분이 여기서 degrade율로 셀 수 있게 된다). ML 비활성(none)이면 애초에 row 를 안 쓴다.
/// </para>
/// </summary>
public sealed record MetricsRow
{
    /// <summary>posture 값 단일 출처 — fail-closed 행 식별자.</summary>
    public const string PostureFailClosed = "fail_closed";

    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    /// <summary>inspect 시각(ISO-8601 UTC) — 짝 <see cref="StoredResult"/> 와 동일 값(조인 키).</summary>
    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }

    /// <summary>VLM 주 판정 — agreement rate·NG recall 집계의 예측 축. fail-closed(VLM-down) 행은 null.</summary>
    [JsonPropertyName("verdict")] public Verdict? Verdict { get; init; }

    /// <summary>VLM self-report 신뢰도(raw). fail-closed 행은 null.</summary>
    [JsonPropertyName("vlm_confidence")] public double? VlmConfidence { get; init; }

    /// <summary>ML 분류 라벨. degrade(분류 실패) 시 null.</summary>
    [JsonPropertyName("ml_label")] public string? MlLabel { get; init; }

    /// <summary>ML softmax 신뢰도(raw). degrade 시 null.</summary>
    [JsonPropertyName("ml_confidence")] public double? MlConfidence { get; init; }

    /// <summary>VLM·ML 일치 여부. degrade 시 null.</summary>
    [JsonPropertyName("agreement")] public bool? Agreement { get; init; }

    /// <summary>불일치/저신뢰 → 검토 필요. degrade 시 null.</summary>
    [JsonPropertyName("requires_review")] public bool? RequiresReview { get; init; }

    /// <summary>ML enabled 인데 분류가 실패(degrade — VLM 단독 진행)했는가. degrade율 집계의 신호.</summary>
    [JsonPropertyName("ml_degraded")] public required bool MlDegraded { get; init; }

    /// <summary>
    /// 운영 자세(③.5 E2) — fail-closed(VLM-down) 행만 "fail_closed". null=비-fail-closed(Proceed/ReviewHold,
    /// 구 행 포함). verdict 기반 집계는 이 행을 제외하고 <c>fail_closed</c>로 별도 카운트한다.
    /// </summary>
    [JsonPropertyName("posture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Posture { get; init; }
}

/// <summary>
/// 시나리오·날짜의 메트릭 집계(B3 read) — <c>GET /api/metrics</c> 응답.
/// <para>
/// <b>원시 카운트 + 파생 비율</b>을 함께 제공한다. 카운트는 근본(날짜 간 합산 가능·0 나눗셈 모호성 없음)이고,
/// 비율은 편의(대시보드 직독)다. 비율은 분모 0 이면 <c>null</c>(undefined 를 0.0 으로 위장하지 않음 — 정직성).
/// </para>
/// <para>
/// NG recall 은 사람 라벨(cycle-37 사이드카)을 정답으로 image_id 조인해 산출한다 — VLM·ML 따로
/// (FW-3 의 "ML &gt; VLM NG recall" 비교가 운영에서도 유지되는지의 데이터 근거, A1 권한이양 입력).
/// </para>
/// </summary>
public sealed record MetricsSummary
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("date")] public required string Date { get; init; }

    /// <summary>ML 활성 inspect 건(비-fail-closed — fail-closed 행 제외). 기존 비율의 분모.</summary>
    [JsonPropertyName("inspections")] public required int Inspections { get; init; }

    /// <summary>ML 분류 실패(degrade)로 2중체크 못 한 건.</summary>
    [JsonPropertyName("ml_degraded")] public required int MlDegraded { get; init; }

    /// <summary>주 검출원(VLM) 사용 불가로 자동 판정 못 한 건(fail-closed).</summary>
    [JsonPropertyName("fail_closed")] public required int FailClosed { get; init; }

    /// <summary>VLM·ML 일치 건(비-degrade 중).</summary>
    [JsonPropertyName("agreements")] public required int Agreements { get; init; }

    /// <summary>검토 필요(불일치/저신뢰) 건(비-degrade 중).</summary>
    [JsonPropertyName("reviews_required")] public required int ReviewsRequired { get; init; }

    /// <summary>블라인드 감사된 라벨 수(consistent+conflicted+resolved) — 일관성 분모.</summary>
    [JsonPropertyName("audited")] public required int Audited { get; init; }

    /// <summary>감사에서 일관(consistent)된 라벨 수.</summary>
    [JsonPropertyName("label_consistent")] public required int LabelConsistent { get; init; }

    /// <summary>미해소 충돌(conflicted) 라벨 수 — 검토 액션 큐.</summary>
    [JsonPropertyName("label_conflicts_open")] public required int LabelConflictsOpen { get; init; }

    /// <summary>사람 라벨이 붙은 건(정답 조인 성공).</summary>
    [JsonPropertyName("labeled")] public required int Labeled { get; init; }

    /// <summary>사람 라벨 = NG 인 건(NG recall 분모 = ground-truth 양성).</summary>
    [JsonPropertyName("labeled_ng")] public required int LabeledNg { get; init; }

    /// <summary>labeled_ng 중 VLM verdict=NG(VLM 이 NG 를 잡은 건).</summary>
    [JsonPropertyName("vlm_ng_hits")] public required int VlmNgHits { get; init; }

    /// <summary>labeled_ng 중 ML 라벨이 존재하는 건(ML recall 분모 — degrade 제외).</summary>
    [JsonPropertyName("ml_ng_scored")] public required int MlNgScored { get; init; }

    /// <summary>labeled_ng 중 ML 라벨=NG(ML 이 NG 를 잡은 건).</summary>
    [JsonPropertyName("ml_ng_hits")] public required int MlNgHits { get; init; }

    // --- 파생 비율(분모 0 → null) -----------------------------------------

    /// <summary>일치율 = agreements / (inspections − ml_degraded). 비-degrade 0 건이면 null.</summary>
    [JsonPropertyName("agreement_rate")] public double? AgreementRate { get; init; }

    /// <summary>검토율 = reviews_required / (inspections − ml_degraded). 비-degrade 0 건이면 null.</summary>
    [JsonPropertyName("review_rate")] public double? ReviewRate { get; init; }

    /// <summary>degrade율 = ml_degraded / inspections. 0 건이면 null.</summary>
    [JsonPropertyName("degrade_rate")] public double? DegradeRate { get; init; }

    /// <summary>VLM NG recall = vlm_ng_hits / labeled_ng. 라벨된 NG 0 건이면 null.</summary>
    [JsonPropertyName("vlm_ng_recall")] public double? VlmNgRecall { get; init; }

    /// <summary>ML NG recall = ml_ng_hits / ml_ng_scored. ML 라벨된 NG 0 건이면 null.</summary>
    [JsonPropertyName("ml_ng_recall")] public double? MlNgRecall { get; init; }

    /// <summary>fail-closed율 = fail_closed / (inspections + fail_closed). 총 시도 0 이면 null.</summary>
    [JsonPropertyName("fail_closed_rate")] public double? FailClosedRate { get; init; }

    /// <summary>라벨 일관성률 = label_consistent / audited. 감사 0 건이면 null(소표본 정직). (C1)</summary>
    [JsonPropertyName("label_consistency_rate")] public double? LabelConsistencyRate { get; init; }
}
