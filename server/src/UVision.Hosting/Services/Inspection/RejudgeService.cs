using System.Globalization;
using UVision.Api.Models;
using UVision.Api.Services.Oracle;
using UVision.Api.Storage;

namespace UVision.Api.Services.Inspection;

/// <summary>
/// 상위 단 재판정 — 저장된 판정 한 건에 사용자가 고른 판정원(<c>vlm</c> 다시 · <c>oracle</c> 더 센 2차 의견 · <c>ml</c> ·
/// <c>memory</c>)을 지금 물어 원 판정과 나란히 놓고 결정적으로 요약한다.
/// <para>
/// <b>판정을 바꾸지 않는다.</b> 저장된 판정 · 메트릭 · 결정 장부 · 사람 라벨 사이드카를 건드리지 않고 별도 기록
/// (<see cref="RejudgeLog"/>)에만 남긴다 — 곡선 · 관문 · 문제 큐가 사용자의 호기심을 판정으로 세지 않게. 원 판정이
/// 바뀌는 길은 사람이 라벨로 정하는 것뿐이다. 판정원 하나의 실패는 그 판정원의 기권으로 담는다.
/// </para>
/// </summary>
public sealed class RejudgeService(
    IScenarioStore scenarios,
    IInspectionStore inspections,
    DecisionEvidenceReader evidence,
    InspectionPipeline pipeline,
    IOracleProvider oracle,
    OracleSweepService oracleSweep,
    RejudgeLog log,
    ILogger<RejudgeService> logger)
{
    /// <summary>오라클 — 더 센 VLM 의 2차 의견. 판정 경로의 단이 아니라 재판정에서만 부를 수 있는 판정원이다.</summary>
    public const string OracleTier = "oracle";

    /// <summary>물을 수 있는 판정원 전부.</summary>
    public static readonly IReadOnlyList<string> SupportedTiers = [.. InspectionPipeline.RejudgeTiers, OracleTier];

    public async Task<RejudgeOutcome> RunAsync(RejudgeRequest request, CancellationToken cancellationToken)
    {
        if (request.Tiers.Count == 0 || request.Tiers.Count > SupportedTiers.Count
            || request.Tiers.Distinct(StringComparer.Ordinal).Count() != request.Tiers.Count)
            return new RejudgeOutcome.Rejected("tiers 는 중복 없는 1개 이상이어야 한다");
        if (request.Tiers.FirstOrDefault(t => !SupportedTiers.Contains(t)) is { } unknown)
            return new RejudgeOutcome.Rejected($"지원하지 않는 단: {unknown} (vlm · oracle · ml · memory)");

        // 형식 위반 id · date 는 ArgumentException — 호출부가 400 으로.
        var scenario = await scenarios.GetAsync(request.ScenarioId, cancellationToken);
        if (scenario is null)
            return new RejudgeOutcome.NotFound("시나리오 없음");
        if (scenario.Type == ScenarioVocabulary.Counting)
            return new RejudgeOutcome.Rejected("계수 시나리오는 검출기가 판정한다 — 재판정을 지원하지 않는다");
        if (request.Tiers.Contains(OracleTier))
        {
            if (!oracle.IsEnabled)
                return new RejudgeOutcome.Rejected("오라클이 꺼져 있다(UVision:Oracle:Provider)");
            if (!OracleSweepPlanner.EgressAllowed(oracle.IsCloud, scenario.AllowCloudEgress))
                return new RejudgeOutcome.Rejected("오라클이 클라우드인데 이 시나리오는 클라우드 송출을 허용하지 않는다(allow_cloud_egress)");
        }

        var original = await evidence.ReadAsync(request.ScenarioId, request.Date, request.ImageId, cancellationToken);
        var image = original is null
            ? null
            : await inspections.ReadImageAsync(request.ScenarioId, request.Date, request.ImageId, cancellationToken);
        if (original is null || image is null)
            return new RejudgeOutcome.NotFound("판정 기록 또는 이미지 없음");
        var record = await inspections.ReadResultAsync(request.ScenarioId, request.Date, request.ImageId, cancellationToken);
        var context = record?.ExpectedPart is null && record?.ObservedCode is null
            ? null
            : new InspectionContext { ExpectedPart = record!.ExpectedPart, ObservedCode = record.ObservedCode };

        var asked = await Task.WhenAll(request.Tiers.Select(t => AskAsync(scenario, request, context, image.Data, t, cancellationToken)));
        var opinions = asked.Select(o => o.Status == "answered"
            ? o with { DiffersFromOriginal = !string.Equals(o.Class, original.Class, StringComparison.OrdinalIgnoreCase) }
            : o).ToList();

        var response = new RejudgeResponse
        {
            ImageId = original.ImageId,
            ScenarioId = original.ScenarioId,
            Date = request.Date,
            RequestedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            OriginalClass = original.Class,
            OriginalVersion = original.TierVersion,
            Opinions = opinions,
            Synthesis = Synthesize(original.Class, opinions),
        };
        try
        {
            await log.AppendAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 기록 실패는 소견을 잃을 이유가 아니다(관측 — degrade). 응답은 그대로 준다.
            logger.LogWarning(ex, "재판정 기록 실패 — 응답만 돌려준다 (scenario={ScenarioId}, image={ImageId})",
                response.ScenarioId, response.ImageId);
        }
        return new RejudgeOutcome.Completed(response);
    }

    private async Task<RejudgeOpinion> AskAsync(
        Scenario scenario, RejudgeRequest request, InspectionContext? context, ReadOnlyMemory<byte> image, string tier,
        CancellationToken cancellationToken)
    {
        if (tier != OracleTier)
            return await pipeline.AskTierAsync(scenario, context, image, tier, cancellationToken);
        try
        {
            var (opinion, label) = await oracleSweep.OpineAsync(scenario, request.Date, request.ImageId, cancellationToken);
            return new RejudgeOpinion
            {
                Tier = tier, Kind = "vlm", Status = "answered", Class = label, Confidence = opinion.Confidence,
                Findings = string.IsNullOrEmpty(opinion.Findings) ? null : opinion.Findings, Version = oracle.Name,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "오라클 재판정 실패 — 기권으로 담는다 (scenario={ScenarioId}, image={ImageId})",
                request.ScenarioId, request.ImageId);
            return InspectionPipeline.Abstained(tier, "vlm", "oracle failed: " + ex.Message, oracle.Name);
        }
    }

    /// <summary>
    /// 결정적 요약 — 문장이 아니라 코드와 표. 원 판정을 한 표로 세고, 답한 판정원이 모두 원 판정과 같은 클래스면
    /// <c>unanimous</c>, 하나라도 다르면 <c>split</c>, 아무도 답하지 못하면 <c>none_answered</c>.
    /// </summary>
    internal static RejudgeSynthesis Synthesize(string originalClass, IReadOnlyList<RejudgeOpinion> opinions)
    {
        var answered = opinions.Where(o => o.Status == "answered").ToList();
        var votes = new SortedDictionary<string, int>(StringComparer.Ordinal) { [originalClass] = 1 };
        foreach (var o in answered)
            votes[o.Class!] = votes.GetValueOrDefault(o.Class!) + 1;
        var differing = answered.Where(o => o.DiffersFromOriginal == true).Select(o => o.Tier).ToList();
        return new RejudgeSynthesis
        {
            Agreement = answered.Count == 0 ? "none_answered" : differing.Count == 0 ? "unanimous" : "split",
            DifferingTiers = differing,
            Answered = answered.Count,
            Votes = votes,
        };
    }
}

/// <summary>재판정의 결과 — 호출부(HTTP)가 자기 표현으로 옮긴다.</summary>
public abstract record RejudgeOutcome
{
    private RejudgeOutcome() { }

    public sealed record Completed(RejudgeResponse Response) : RejudgeOutcome;

    /// <summary>레코드 · 이미지 · 시나리오가 없다(404).</summary>
    public sealed record NotFound(string Reason) : RejudgeOutcome;

    /// <summary>요청이 이 설치에서 할 수 없는 것이다(400) — 단 이름, 계수 시나리오, 오라클 꺼짐, 송출 불허.</summary>
    public sealed record Rejected(string Reason) : RejudgeOutcome;
}
