using System.Collections.Concurrent;
using HoneAI;
using HoneAI.Trust;
using UVision.Api.Models;
using UVision.Api.Services.Inspection;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;

namespace UVision.Api.Services.Questions;

/// <summary>
/// 사람이 푸는 단 — 정답 심기 · 재확인에서 «판정한 단» 이다. <b>id 는 한 번 영속하면 바꿀 수 없다</b>: 정답 심기 기록의 단 id 가 되고
/// 교사 자격 장부가 그 id 로 거른다. 판(<see cref="Tier.Version"/>)은 질문 카드 · 지침의 판이고, 카드 형식이 바뀌면 올린다.
/// </summary>
public static class HumanTier
{
    public const string Id = "people";

    public static readonly Tier Tier = new() { Id = Id, Kind = JudgeKind.Human, Version = "card:single/1", CostRank = 100 };
}

/// <summary>
/// 정답 심기 — 정답을 아는 기준 이미지를 일반 문제와 같은 카드로 내고, 풀이자의 답을 재확인 풀(<c>HoneAI</c>)과 정답 심기 기록에 적는다.
/// 심기는 풀이자 신뢰(정답률)를 재는 것이며 판정 · 라벨 · 학습 어느 것도 바꾸지 않는다: 심은 문제의 답은 사람 라벨이 되지 않는다.
/// 켜는 곳은 <see cref="UVision.Api.Configuration.QuestionsOptions.PlantRate"/> 하나다(기본 0).
/// </summary>
public sealed class PlantFlow(IReferenceStore references, StoragePaths paths, TimeProvider? clock = null)
{
    // 풀 항목의 만료 · 임대 길이 — 심기는 한 장치에 바로 나가 같은 요청 흐름에서 풀리므로 길 필요가 없다. 응답률을 모르는 지금의 안전한 값.
    private static readonly TimeSpan Hold = TimeSpan.FromDays(7);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, JsonlRecheckPoolStore> _pools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonlPlantStore> _plants = new(StringComparer.Ordinal);

    /// <summary>
    /// 이 장치에 심을 기준 이미지를 고른다. 시나리오의 기준 이미지 가운데 정답이 라벨에 있는 것이 없으면 null(심지 않고 일반 문제로).
    /// </summary>
    public async Task<PlantCandidate?> PickAsync(
        Scenario scenario, EffectiveScenario effective, IReadOnlyList<StoredQuestion> asked, string by, Random rng,
        CancellationToken cancellationToken)
    {
        var refs = await references.ListAsync(scenario.ScenarioId, cancellationToken);
        var candidates = PlantPicker.Candidates(refs, scenario.NgLabels, effective.Labels);
        var planted = asked.Where(q => q.Source == QuestionSource.Reference && q.AskedTo == by && q.SourceRef is not null)
            .Select(q => q.SourceRef!).ToList();
        return PlantPicker.Pick(candidates, planted, rng);
    }

    /// <summary>심은 문제를 풀에 넣고 장치에 임대한다 — 풀의 응답률 회계가 «닿음» 을 센다.</summary>
    public async Task RecordAskedAsync(StoredQuestion question, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var pool = PoolOf(question.ScenarioId);
        await pool.AddAsync(new PoolItem
        {
            DecisionId = question.QuestionId, Kind = PoolItemKind.Plant, TierId = HumanTier.Id, SliceKey = question.ScenarioId,
            DecidedAt = now, Probability = 1.0, Knob = "", KnobRates = new Dictionary<string, double>(), SnapshotVersion = "",
            InputRef = question.QuestionId, ExpiresAt = now + Hold,
        }, cancellationToken);
        await pool.LeaseAsync(new PoolLease(question.QuestionId, HumanTier.Id, Reviewer(question.AskedTo), now + Hold), cancellationToken);
    }

    /// <summary>
    /// 심은 문제의 답을 적는다. 고른 클래스는 정답과 견줘 정답 심기 기록(<c>Correct</c> / <c>Wrong</c>)이 되고, «모르겠음» 은 풀의
    /// <c>Unsure</c>(닿음에 세고 라벨도 실패도 아님)로만 남는다.
    /// </summary>
    public async Task RecordAnsweredAsync(
        StoredQuestion question, EffectiveScenario effective, string? choice, string by, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var pool = PoolOf(question.ScenarioId);
        var shown = new PoolPresentation(question.QuestionId, HumanTier.Id, question.ScenarioId, question.QuestionId, PoolPurpose.Plant);
        var reviewer = Reviewer(by);

        if (choice is null)
        {
            await pool.RespondAsync(PoolAnswers.Unsure(shown, HumanTier.Id, reviewer, now), cancellationToken);
            return;
        }

        var truth = question.SourceTruth ?? throw new InvalidOperationException("심은 문제에 정답이 없다: " + question.QuestionId);
        var policy = DecisionLedger.PolicyFor(effective);
        var verdict = JudgeVerdict<string>.Answer(choice, null, null, null, HumanTier.Id);
        await pool.RespondAsync(PoolAnswers.Response(shown, HumanTier.Id, reviewer, verdict, truth, policy, now), cancellationToken);

        var result = PoolAnswers.Result(shown, HumanTier.Tier, verdict, now);
        var record = PlantRecords.From(result, truth, question.ScenarioId, policy, QuestionSource.Reference);
        await PlantsOf(question.ScenarioId).AppendAsync(record, cancellationToken);
    }

    // 장치 id 가 비어 있으면(익명 풀이자) 풀 응답의 풀이자 키가 비지 않게 한다.
    private static string Reviewer(string by) => string.IsNullOrEmpty(by) ? "device" : by;

    private JsonlRecheckPoolStore PoolOf(string scenarioId) =>
        _pools.GetOrAdd(scenarioId, id => new JsonlRecheckPoolStore(paths.PoolDir(id)));

    private JsonlPlantStore PlantsOf(string scenarioId) =>
        _plants.GetOrAdd(scenarioId, id => new JsonlPlantStore(Path.Combine(paths.PlantsDir(id), "plants.jsonl")));
}
