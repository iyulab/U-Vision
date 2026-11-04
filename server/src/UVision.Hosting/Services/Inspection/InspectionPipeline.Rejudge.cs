using UVision.Api.Models;
using UVision.Api.Services.Scenarios;
using UVision.Api.Services.Vlm;

namespace UVision.Api.Services.Inspection;

/// <summary>
/// 재판정 — 저장된 판정에 판정원 한 곳을 지금 다시 물어본다(<see cref="RejudgeService"/> 가 부른다). 판정과 **같은 방식으로**
/// 묻는다(VLM 입력 조립 · 클래스 걸러내기 · 판 지문을 판정 경로와 공유) — 그래서 «같은 판인가» 가 비교된다.
/// 결과를 어디에도 쓰지 않는다: 호출부가 재판정 기록에 따로 적는다.
/// </summary>
public sealed partial class InspectionPipeline
{
    /// <summary>재판정으로 물을 수 있는 이 파이프라인의 판정원.</summary>
    internal static readonly IReadOnlyList<string> RejudgeTiers =
        [ScenarioVocabulary.VlmTier, ScenarioVocabulary.MlTier, ScenarioVocabulary.MemoryTier];

    /// <summary>
    /// 판정원 하나에 물어 답 또는 기권(사유)을 돌려준다. 예외를 던지지 않는다 — 판정원의 실패는 기권으로 담는다.
    /// </summary>
    internal async Task<RejudgeOpinion> AskTierAsync(
        Scenario scenario, InspectionContext? inspectionContext, ReadOnlyMemory<byte> image, string tier,
        CancellationToken cancellationToken)
    {
        var effective = EffectiveScenario.Resolve(scenario, classifier.IsEnabled, memory.IsEnabled);
        switch (tier)
        {
            case ScenarioVocabulary.VlmTier:
            {
                var (vlmImage, context, references) = await PrepareVlmInputAsync(
                    scenario, effective, image, inspectionContext, cancellationToken);
                var version = TeacherEdition.Of(
                    $"{provider.Name}:{vlmOptions.Model}", vlmOptions, context, scenario.MaxImageDimension, references);
                var answer = AcceptVlm(await InspectSafelyAsync(vlmImage, context, cancellationToken), effective);
                return answer is null
                    ? Abstained(tier, "vlm", "vlm unavailable", version)
                    : new RejudgeOpinion
                    {
                        Tier = tier, Kind = "vlm", Status = "answered",
                        Class = effective.ClassOf(answer) ?? effective.ClassOf(answer.Verdict),
                        Confidence = answer.Confidence,
                        Findings = string.IsNullOrEmpty(answer.Findings) ? null : answer.Findings,
                        Version = version,
                    };
            }
            case ScenarioVocabulary.MlTier:
            {
                if (!classifier.IsEnabled)
                    return Abstained(tier, "ml", "ml disabled", null);
                var outcome = AcceptMl(await ClassifySafelyAsync(image, scenario.ScenarioId, cancellationToken), effective);
                return outcome.Result is { } c
                    ? new RejudgeOpinion
                    {
                        Tier = tier, Kind = "ml", Status = "answered",
                        Class = effective.ClassFor(c.Label), Confidence = c.Confidence,
                        Version = c.ModelVersion ?? classifier.Name,
                    }
                    : Abstained(tier, "ml", "ml failed: " + outcome.FailureReason, classifier.Name);
            }
            case ScenarioVocabulary.MemoryTier:
            {
                if (!memory.IsEnabled)
                    return Abstained(tier, "system1", "memory disabled", null);
                var recalled = await memory.RecallAsync(effective, image, cancellationToken);
                return recalled.Answer is { Class: { } cls } answer
                    ? new RejudgeOpinion
                    {
                        Tier = tier, Kind = "system1", Status = "answered",
                        Class = cls, Confidence = answer.Confidence, Version = recalled.EncoderKey,
                    }
                    : Abstained(tier, "system1", recalled.AbstainReason ?? "abstained", recalled.EncoderKey);
            }
            default:
                throw new ArgumentException($"재판정을 지원하지 않는 단: {tier}", nameof(tier));
        }
    }

    internal static RejudgeOpinion Abstained(string tier, string kind, string reason, string? version) => new()
    {
        Tier = tier, Kind = kind, Status = "abstained", Reason = reason, Version = version,
    };
}
