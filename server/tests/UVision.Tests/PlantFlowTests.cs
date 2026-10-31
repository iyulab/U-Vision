using HoneAI;
using HoneAI.Trust;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Questions;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>정답 심기의 순수 선택 — 심을지 · 어느 기준 이미지를 · 장치마다 외우지 않게.</summary>
public class PlantPickerTests
{
    private static ReferenceInfo Ref(string id, ReferenceLabel label) => new() { RefId = id, Label = label };

    private static readonly IReadOnlyDictionary<string, string> NoTypes = new Dictionary<string, string>();

    [Fact]
    public void ShouldPlant_IsFalseAtRateZero_WithoutDrawingFromTheGenerator()
    {
        var used = new Random(5);
        var untouched = new Random(5);

        Assert.False(PlantPicker.ShouldPlant(0, used));

        Assert.Equal(untouched.Next(), used.Next()); // 꺼져 있으면 난수도 소비하지 않는다 — 기본 상태가 심기 없던 때와 같다
    }

    [Fact]
    public void ShouldPlant_IsAlwaysTrueAtRateOne()
    {
        var rng = new Random(1);
        for (var i = 0; i < 50; i++)
            Assert.True(PlantPicker.ShouldPlant(1, rng));
    }

    [Fact]
    public void Candidates_TakeTheTruthFromTheReference_OkIsOk_AndANgWithoutATypeIsNg()
    {
        var c = PlantPicker.Candidates([Ref("a", ReferenceLabel.Ok), Ref("b", ReferenceLabel.Ng)], NoTypes, ["OK", "NG"]);

        Assert.Equal([new PlantCandidate("a", "OK"), new PlantCandidate("b", "NG")], c);
    }

    [Fact]
    public void Candidates_UseTheDeclaredDefectType_AndSkipATypeTheScenarioDoesNotDeclare()
    {
        var types = new Dictionary<string, string> { ["b"] = "scratch", ["c"] = "ghost" };

        var c = PlantPicker.Candidates(
            [Ref("a", ReferenceLabel.Ok), Ref("b", ReferenceLabel.Ng), Ref("c", ReferenceLabel.Ng)], types, ["OK", "scratch"]);

        // «ghost» 는 이 시나리오의 라벨이 아니라 풀이자가 고를 수 없다 — 채점할 수 없으니 심지 않는다.
        Assert.Equal([new PlantCandidate("a", "OK"), new PlantCandidate("b", "scratch")], c);
    }

    [Fact]
    public void Pick_PrefersTheLeastPlantedForTheDevice_AndRepeatsOnlyAfterAllWereUsed()
    {
        PlantCandidate[] candidates = [new("a", "OK"), new("b", "NG"), new("c", "OK")];
        var rng = new Random(3);

        Assert.Equal("c", PlantPicker.Pick(candidates, ["a", "b"], rng)!.RefId);          // 아직 안 나간 것
        Assert.Equal("b", PlantPicker.Pick(candidates, ["a", "c", "a", "c"], rng)!.RefId); // 가장 덜 나간 것
        Assert.Contains(PlantPicker.Pick(candidates, ["a", "b", "c"], rng)!.RefId, new[] { "a", "b", "c" }); // 다 썼으면 되풀이
    }

    [Fact]
    public void Pick_IsNullWithoutCandidates()
    {
        Assert.Null(PlantPicker.Pick([], [], new Random(1)));
    }
}

/// <summary>정답 심기 흐름 — 문제 서비스 위에서: 기본 꺼짐 · 같은 카드로 내기 · 답이 라벨이 아니라 정답률로 적히기.</summary>
public class PlantFlowTests : IDisposable
{
    private const string Scenario = "demo";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvtest_plant_" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FileLabelStore _labels;
    private readonly FileQuestionStore _questions;
    private readonly FileReferenceStore _references;
    private readonly FakeSource _source = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    public PlantFlowTests()
    {
        _paths = new StoragePaths(new StorageOptions { DataPath = _root }, _root);
        _labels = new FileLabelStore(_paths);
        _questions = new FileQuestionStore(_paths);
        _references = new FileReferenceStore(_paths);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private QuestionService Service(double plantRate, Dictionary<string, string>? ngLabels = null) =>
        new(new FakeScenarios(ngLabels), _labels, _questions, _source, new QuestionsOptions { PlantRate = plantRate },
            _clock, new Random(7), new PlantFlow(_references, _paths, _clock));

    private async Task<string> SaveRefAsync(ReferenceLabel label) =>
        await _references.SaveAsync(Scenario, label, Jpeg, ".jpg");

    private static async Task<StoredQuestion> AskedAsync(QuestionService s, string by = "dev") =>
        Assert.IsType<QuestionOutcome.Asked>(await s.NextAsync(Scenario, by, default)).Question;

    private async Task<List<PoolEntry>> PoolEntriesAsync()
    {
        var all = new List<PoolEntry>();
        await foreach (var e in new JsonlRecheckPoolStore(_paths.PoolDir(Scenario)).ReadAsync(
            _clock.GetUtcNow().AddDays(-1), _clock.GetUtcNow().AddDays(1), default))
            all.Add(e);
        return all;
    }

    private async Task<List<PlantRecord>> PlantRecordsAsync()
    {
        var all = new List<PlantRecord>();
        await foreach (var r in new JsonlPlantStore(Path.Combine(_paths.PlantsDir(Scenario), "plants.jsonl")).ReadAsync(
            HumanTier.Id, _clock.GetUtcNow().AddDays(-1), _clock.GetUtcNow().AddDays(1), default))
            all.Add(r);
        return all;
    }

    [Fact]
    public async Task AtTheDefaultRate_NothingIsPlanted_EvenWithReferences_AndNoPoolOrPlantFilesAreMade()
    {
        await SaveRefAsync(ReferenceLabel.Ok);
        _source.Candidates = [new QuestionCandidate("p1", "2026-10-31", null)];

        var q = await AskedAsync(Service(plantRate: 0));

        Assert.Null(q.Source);
        Assert.Equal("p1", q.ImageId);
        Assert.False(Directory.Exists(_paths.PoolDir(Scenario)));
        Assert.False(Directory.Exists(_paths.PlantsDir(Scenario)));
    }

    [Fact]
    public async Task APlantedQuestion_IsAReferenceImageWithItsTruth_PooledAndLeasedToTheDevice()
    {
        var refOk = await SaveRefAsync(ReferenceLabel.Ok);

        var q = await AskedAsync(Service(plantRate: 1), "tablet-1");

        Assert.Equal(QuestionSource.Reference, q.Source);
        Assert.Equal(refOk, q.SourceRef);
        Assert.Equal("OK", q.SourceTruth);
        Assert.Equal(QuestionReason.Plant, q.Reason);
        Assert.Equal(QuestionState.Open, q.State);
        var entry = Assert.Single(await PoolEntriesAsync());
        Assert.Equal(q.QuestionId, entry.Item.DecisionId);
        Assert.Equal(PoolItemKind.Plant, entry.Item.Kind);
        Assert.Equal(HumanTier.Id, entry.Item.TierId);
        Assert.Equal("tablet-1", entry.Lease!.ReviewerKey);
    }

    [Fact]
    public async Task APlantedQuestionStaysOpen_AndIsShownAgainToTheSameDevice()
    {
        await SaveRefAsync(ReferenceLabel.Ok);
        var s = Service(plantRate: 1);

        var first = await AskedAsync(s, "tablet-1");
        var again = await AskedAsync(s, "tablet-1");

        Assert.Equal(first.QuestionId, again.QuestionId); // 새로고침으로 문제를 건너뛰지 않는다 — 심은 문제도 같다
        Assert.Single(await PoolEntriesAsync());
    }

    [Fact]
    public async Task ACorrectAnswer_IsAPlantRecord_NotALabel_AndClosesTheQuestion()
    {
        await SaveRefAsync(ReferenceLabel.Ok);
        var s = Service(plantRate: 1);
        var q = await AskedAsync(s, "tablet-1");

        var outcome = await s.AnswerAsync(Scenario, q.QuestionId, "OK", abstain: false, "tablet-1", default);

        var recorded = Assert.IsType<AnswerOutcome.Recorded>(outcome);
        Assert.Equal("label", recorded.Kind); // 풀이자에게는 일반 문제와 같은 응답
        var record = Assert.Single(await PlantRecordsAsync());
        Assert.Equal(PlantOutcome.Correct, record.Outcome);
        Assert.Equal(HumanTier.Id, record.TierId);
        Assert.Equal(QuestionSource.Reference, record.TruthSource);
        Assert.Empty(await _labels.ListAsync(Scenario, "2026-10-31")); // 사람 라벨이 되지 않는다(날짜 화이트리스트가 «reference» 로는 라벨 경로를 못 만든다)
        Assert.Equal(QuestionState.Answered, (await _questions.ReadAsync(Scenario, q.QuestionId))!.State);
        // 풀 응답은 «닿음» 의 근거다 — 정답 심기의 채점(Correct / Wrong)은 풀 응답이 아니라 정답 심기 기록에 있다(풀 응답의 Outcome 은 비어 있다).
        var response = Assert.Single(Assert.Single(await PoolEntriesAsync()).ResponsesOf(PoolPurpose.Plant));
        Assert.Equal(PoolResponseKind.Answered, response.Kind);
        Assert.Equal("tablet-1", response.ReviewerKey);
        Assert.Equal(HumanTier.Id, response.CheckedBy);
    }

    [Fact]
    public async Task AWrongAnswer_IsRecordedAsWrong()
    {
        await SaveRefAsync(ReferenceLabel.Ng);
        var s = Service(plantRate: 1);
        var q = await AskedAsync(s);
        Assert.Equal("NG", q.SourceTruth);

        await s.AnswerAsync(Scenario, q.QuestionId, "OK", abstain: false, "dev", default);

        Assert.Equal(PlantOutcome.Wrong, Assert.Single(await PlantRecordsAsync()).Outcome);
        Assert.Equal(PoolResponseKind.Answered, Assert.Single(Assert.Single(await PoolEntriesAsync()).ResponsesOf(PoolPurpose.Plant)).Kind);
    }

    [Fact]
    public async Task DontKnow_IsAnUnsureResponseInThePool_AndNoPlantRecord()
    {
        await SaveRefAsync(ReferenceLabel.Ok);
        var s = Service(plantRate: 1);
        var q = await AskedAsync(s);

        var outcome = await s.AnswerAsync(Scenario, q.QuestionId, null, abstain: true, "dev", default);

        Assert.Equal("abstain", Assert.IsType<AnswerOutcome.Recorded>(outcome).Kind);
        Assert.Empty(await PlantRecordsAsync()); // «모르겠음» 은 정답률에 세지 않는다 — 도달에만 센다
        var response = Assert.Single(Assert.Single(await PoolEntriesAsync()).ResponsesOf(PoolPurpose.Plant));
        Assert.Equal(PoolResponseKind.Unsure, response.Kind);
        Assert.Equal(QuestionState.Abstained, (await _questions.ReadAsync(Scenario, q.QuestionId))!.State);
    }

    [Fact]
    public async Task ADevice_IsNotShownTheSameReferenceTwice_UntilAllWereUsed()
    {
        var a = await SaveRefAsync(ReferenceLabel.Ok);
        var b = await SaveRefAsync(ReferenceLabel.Ng);
        var s = Service(plantRate: 1);

        var first = await AskedAsync(s, "tablet-1");
        await s.AnswerAsync(Scenario, first.QuestionId, "OK", abstain: false, "tablet-1", default);
        var second = await AskedAsync(s, "tablet-1");

        Assert.NotEqual(first.SourceRef, second.SourceRef);
        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), new[] { first.SourceRef!, second.SourceRef! }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WithoutAPlantableReference_ARegularQuestionIsAskedInstead()
    {
        // 기준 이미지가 하나도 없다 — 확률이 1 이어도 심을 것이 없으니 일반 문제로.
        _source.Candidates = [new QuestionCandidate("p1", "2026-10-31", null)];

        var q = await AskedAsync(Service(plantRate: 1));

        Assert.Null(q.Source);
        Assert.Equal("p1", q.ImageId);
    }

    [Fact]
    public async Task AnNgReferenceWithADeclaredType_IsPlantedWithThatTypeAsTheTruth()
    {
        var ng = await SaveRefAsync(ReferenceLabel.Ng);
        var s = new QuestionService(
            new FakeScenarios(new Dictionary<string, string> { [ng] = "scratch" }, classes: ["OK", "scratch"]), _labels, _questions, _source,
            new QuestionsOptions { PlantRate = 1 }, _clock, new Random(7), new PlantFlow(_references, _paths, _clock));

        var q = await AskedAsync(s);

        Assert.Equal("scratch", q.SourceTruth);
    }

    private sealed class FakeSource : IQuestionCandidateSource
    {
        public IReadOnlyList<QuestionCandidate> Candidates { get; set; } = [];

        public Task<IReadOnlyList<QuestionCandidate>> ListAsync(
            string scenarioId, IReadOnlyList<string> dates, CancellationToken cancellationToken) => Task.FromResult(Candidates);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeScenarios(Dictionary<string, string>? ngLabels = null, string[]? classes = null) : IScenarioStore
    {
        public Task<Scenario?> GetAsync(string scenarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Scenario?>(scenarioId == Scenario
                ? new Scenario
                {
                    ScenarioId = Scenario, Name = "demo", Criteria = "c", NgLabels = ngLabels ?? new(),
                    Classes = classes?.Select(c => new ScenarioClass { Id = c, Settleable = c == "OK", MustCatch = c != "OK" }).ToList(),
                    Type = classes is null ? null : ScenarioVocabulary.Inspection,
                }
                : null);

        public Task<IReadOnlyList<Scenario>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Scenario?> CreateAsync(ScenarioInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Scenario?> UpdateAsync(string scenarioId, ScenarioInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string scenarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetNgLabelAsync(string scenarioId, string referenceId, string? label, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
