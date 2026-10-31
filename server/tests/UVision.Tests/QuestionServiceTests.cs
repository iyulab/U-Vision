using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Questions;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public class QuestionServiceTests : IDisposable
{
    private const string Scenario = "demo";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvtest_" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FileLabelStore _labels;
    private readonly FileQuestionStore _questions;
    private readonly FakeSource _source = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    public QuestionServiceTests()
    {
        _paths = new StoragePaths(new StorageOptions { DataPath = _root }, _root);
        _labels = new FileLabelStore(_paths);
        _questions = new FileQuestionStore(_paths);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private QuestionService Service(QuestionsOptions? options = null, string? type = null, int seed = 1) =>
        new(new FakeScenarios(type), _labels, _questions, _source, options ?? new QuestionsOptions(), _clock, new Random(seed));

    private static QuestionCandidate C(string id, double? u = null, string date = "2026-10-31") => new(id, date, u);

    private static async Task<StoredQuestion> AskedAsync(QuestionService s, string by = "dev")
    {
        var outcome = await s.NextAsync(Scenario, by, default);
        return Assert.IsType<QuestionOutcome.Asked>(outcome).Question;
    }

    [Fact]
    public async Task RandomRule_AsksFromThePool_RecordsReasonRandom_AndNoScore()
    {
        _source.Candidates = [C("a"), C("b"), C("c")];

        var q = await AskedAsync(Service(new QuestionsOptions { Rule = QuestionsOptions.RandomRule }));

        Assert.Contains(q.ImageId, new[] { "a", "b", "c" });
        Assert.Equal(QuestionReason.Random, q.Reason);
        Assert.Null(q.Score);
        Assert.Equal(3, q.PoolSize);
        Assert.Null(q.RandomFloor);
        Assert.Equal(LabelStream.Learn, q.Stream);
        Assert.Equal(QuestionForm.Single, q.Form);
        Assert.Equal(QuestionState.Open, q.State);
    }

    [Fact]
    public async Task Next_SearchesTheConfiguredWindowEndingToday()
    {
        _source.Candidates = [C("a")];

        await Service(new QuestionsOptions { WindowDays = 3 }).NextAsync(Scenario, "dev", default);

        Assert.Equal(["2026-10-31", "2026-10-30", "2026-10-29"], _source.LastDates);
    }

    [Fact]
    public async Task Next_SkipsLabeledAbstainedAndAlreadyAskedPhotos()
    {
        await _labels.AppendLabelAsync(Scenario, "2026-10-31", "labeled", "OK", "dev");
        await _labels.AppendAbstainAsync(Scenario, "2026-10-31", "abstained", "dev");
        _source.Candidates = [C("labeled"), C("abstained"), C("asked"), C("fresh")];
        var s = Service();
        // "asked" 는 이미 물었고 답까지 받은 사진이다.
        await _questions.CreateAsync(new StoredQuestion
        {
            QuestionId = "q0", ScenarioId = Scenario, Date = "2026-10-31", ImageId = "asked", Form = QuestionForm.Single,
            Stream = LabelStream.Learn, Reason = QuestionReason.Random, CreatedAt = "2026-10-31T10:00:00.0000000Z",
            AskedTo = "other", State = QuestionState.Answered,
        });

        Assert.Equal("fresh", (await AskedAsync(s)).ImageId);
        // 열린 문제 하나가 남아 있어도 풀이자가 다르면 후보는 이제 없다.
        Assert.IsType<QuestionOutcome.NothingToAsk>(await s.NextAsync(Scenario, "second-device", default));
    }

    [Fact]
    public async Task Next_ShowsTheSameOpenQuestionAgain_ToTheSameDevice()
    {
        _source.Candidates = [C("a"), C("b"), C("c")];
        var s = Service();

        var first = await AskedAsync(s, "tablet-1");
        var again = await AskedAsync(s, "tablet-1");
        var other = await AskedAsync(s, "tablet-2");

        Assert.Equal(first.QuestionId, again.QuestionId);
        Assert.NotEqual(first.ImageId, other.ImageId);
    }

    [Fact]
    public async Task AnswerWithAClass_WritesALearnStreamLabel_AndClosesTheQuestion()
    {
        _source.Candidates = [C("a")];
        var s = Service();
        var q = await AskedAsync(s);

        var outcome = await s.AnswerAsync(Scenario, q.QuestionId, "NG", abstain: false, "dev", default);

        Assert.Equal("label", Assert.IsType<AnswerOutcome.Recorded>(outcome).Kind);
        var label = await _labels.ReadAsync(Scenario, q.Date, q.ImageId);
        Assert.Equal("NG", label!.OperativeLabel);
        Assert.True(label.IsLearnStream);
        var closed = await _questions.ReadAsync(Scenario, q.QuestionId);
        Assert.Equal(QuestionState.Answered, closed!.State);
        Assert.Equal("NG", closed.Answer);
        Assert.Equal("dev", closed.AnsweredBy);
    }

    [Fact]
    public async Task AnswerWithAbstain_WritesAnAbstentionNotALabel()
    {
        _source.Candidates = [C("a")];
        var s = Service();
        var q = await AskedAsync(s);

        var outcome = await s.AnswerAsync(Scenario, q.QuestionId, null, abstain: true, "dev", default);

        Assert.Equal("abstain", Assert.IsType<AnswerOutcome.Recorded>(outcome).Kind);
        var label = await _labels.ReadAsync(Scenario, q.Date, q.ImageId);
        Assert.Null(label!.OperativeLabel);
        Assert.Equal(1, label.AbstainCount);
        Assert.Equal(QuestionState.Abstained, (await _questions.ReadAsync(Scenario, q.QuestionId))!.State);
        // 기권한 사진은 다시 묻지 않는다.
        Assert.IsType<QuestionOutcome.NothingToAsk>(await s.NextAsync(Scenario, "dev", default));
    }

    [Theory]
    [InlineData("NG", true)]   // 둘 다
    [InlineData(null, false)]  // 둘 다 아님
    [InlineData("MAYBE", false)] // 허용되지 않은 클래스
    public async Task InvalidAnswers_AreRejected_AndLeaveTheQuestionOpen(string? choice, bool abstain)
    {
        _source.Candidates = [C("a")];
        var s = Service();
        var q = await AskedAsync(s);

        var outcome = await s.AnswerAsync(Scenario, q.QuestionId, choice, abstain, "dev", default);

        Assert.IsType<AnswerOutcome.InvalidChoice>(outcome);
        Assert.Equal(QuestionState.Open, (await _questions.ReadAsync(Scenario, q.QuestionId))!.State);
        Assert.Null(await _labels.ReadAsync(Scenario, q.Date, q.ImageId));
    }

    [Fact]
    public async Task AnsweringTwice_IsRefused_AndKeepsTheFirstAnswer()
    {
        _source.Candidates = [C("a")];
        var s = Service();
        var q = await AskedAsync(s);
        await s.AnswerAsync(Scenario, q.QuestionId, "OK", false, "dev", default);

        var second = await s.AnswerAsync(Scenario, q.QuestionId, "NG", false, "other", default);

        Assert.IsType<AnswerOutcome.AlreadyClosed>(second);
        Assert.Equal("OK", (await _labels.ReadAsync(Scenario, q.Date, q.ImageId))!.OperativeLabel);
    }

    [Fact]
    public async Task AnswerAfterThePhotoWasLabeledElsewhere_WithdrawsTheQuestion_AndKeepsTheLabel()
    {
        _source.Candidates = [C("a")];
        var s = Service();
        var q = await AskedAsync(s);
        await _labels.AppendLabelAsync(Scenario, q.Date, q.ImageId, "NG", "worker"); // 결과 목록에서 먼저 라벨을 달았다

        var outcome = await s.AnswerAsync(Scenario, q.QuestionId, "OK", false, "dev", default);

        Assert.IsType<AnswerOutcome.AlreadyLabeled>(outcome);
        var label = await _labels.ReadAsync(Scenario, q.Date, q.ImageId);
        Assert.Equal("NG", label!.OperativeLabel);
        Assert.False(label.IsLearnStream);
        Assert.Equal(QuestionState.Withdrawn, (await _questions.ReadAsync(Scenario, q.QuestionId))!.State);
    }

    [Fact]
    public async Task UncertainRule_AsksTheLeastSurePhoto_AndRecordsItsScoreAsTheReason()
    {
        _source.Candidates = [C("sure", 0.01), C("unsure", 1.05), C("far", 0.40)];

        var q = await AskedAsync(Service(new QuestionsOptions { Rule = QuestionsOptions.UncertainRule, RandomFloor = 0 }));

        Assert.Equal("unsure", q.ImageId);
        Assert.Equal(QuestionReason.Uncertain, q.Reason);
        Assert.Equal(1.05, q.Score);
        Assert.Equal(3, q.PoolSize);
        Assert.Equal(0.0, q.RandomFloor);
    }

    [Fact]
    public async Task UncertainRule_StartsAtRandomWhenTheMemoryLeftNoSignal()
    {
        _source.Candidates = [C("a"), C("b")];

        var q = await AskedAsync(Service(new QuestionsOptions { Rule = QuestionsOptions.UncertainRule, RandomFloor = 0 }));

        Assert.Equal(QuestionReason.ColdStart, q.Reason);
        Assert.Null(q.Score);
    }

    [Fact]
    public async Task VerificationScenarios_AreNotSupported_AndMissingOnesAreReported()
    {
        _source.Candidates = [C("a")];

        Assert.IsType<QuestionOutcome.Unsupported>(await Service(type: "verification").NextAsync(Scenario, "dev", default));
        Assert.IsType<QuestionOutcome.ScenarioMissing>(await Service().NextAsync("nope", "dev", default));
        Assert.IsType<AnswerOutcome.ScenarioMissing>(await Service().AnswerAsync("nope", "q", "OK", false, "dev", default));
        Assert.IsType<AnswerOutcome.QuestionMissing>(await Service().AnswerAsync(Scenario, "no-such-question", "OK", false, "dev", default));
    }

    [Fact]
    public async Task CountingScenarios_AreNotSupported_BecauseTheirOkNgIsDerivedFromTheCount()
    {
        _source.Candidates = [C("a")];

        var outcome = await Service(type: "counting").NextAsync(Scenario, "dev", default);

        var unsupported = Assert.IsType<QuestionOutcome.Unsupported>(outcome);
        Assert.Contains("개수", unsupported.Reason);
        Assert.Empty(await _questions.ListAsync(Scenario, default));
    }

    [Fact]
    public void Options_RejectUnknownRulesAndOutOfRangeValues()
    {
        Assert.Throws<ArgumentException>(() => new QuestionsOptions { Rule = "magic" }.Validate());
        Assert.Throws<ArgumentException>(() => new QuestionsOptions { RandomFloor = 1.5 }.Validate());
        Assert.Throws<ArgumentException>(() => new QuestionsOptions { RandomFloor = double.NaN }.Validate());
        Assert.Throws<ArgumentException>(() => new QuestionsOptions { WindowDays = 0 }.Validate());
        new QuestionsOptions().Validate(); // 기본값은 유효하다
    }

    [Fact]
    public async Task Next_PicksAnotherPhoto_WhenTheTopRankedOneWasLabeledElsewhere()
    {
        await _labels.AppendLabelAsync(Scenario, "2026-10-31", "top", "OK", "dev");
        _source.Candidates = [C("top", 9.0), C("next", 5.0), C("low", 1.0)];

        var q = await AskedAsync(Service(new QuestionsOptions { RandomFloor = 0 }));

        Assert.Equal("next", q.ImageId);
    }

    [Fact]
    public async Task Next_FallsBackToTheLabelListWhenManyTopRankedPhotosAreLabeled()
    {
        // 순위 위쪽 40장에 이미 라벨이 있다 — 사진마다 확인하는 횟수 상한을 넘으면 목록으로 한 번에 거른다.
        var labeled = Enumerable.Range(0, 40).Select(i => C($"l{i:D2}", 100.0 - i)).ToList();
        foreach (var c in labeled)
            await _labels.AppendLabelAsync(Scenario, c.Date, c.ImageId, "OK", "dev");
        _source.Candidates = [.. labeled, C("only", 0.5)];

        var q = await AskedAsync(Service(new QuestionsOptions { RandomFloor = 0 }));

        Assert.Equal("only", q.ImageId);
    }

    [Fact]
    public async Task Next_HasNothingToAsk_WhenEveryCandidateIsLabeled()
    {
        var all = Enumerable.Range(0, 30).Select(i => C($"x{i:D2}", i)).ToList();
        foreach (var c in all)
            await _labels.AppendLabelAsync(Scenario, c.Date, c.ImageId, "OK", "dev");
        _source.Candidates = all;

        Assert.IsType<QuestionOutcome.NothingToAsk>(await Service(new QuestionsOptions { RandomFloor = 0 }).NextAsync(Scenario, "dev", default));
    }

    private static QuestionCandidate P(string id, double patch, string date = "2026-10-31") => new(id, date, null, patch);

    // 대표 표본(출제 이유가 reason)으로 정상(OK)이라 답한 사진 n 장 — 패치 점수가 0.10 + 0.01 i 로 퍼져 있다.
    private async Task<List<QuestionCandidate>> AnsweredOkAsync(int n, string reason, string prefix = "ok")
    {
        var done = new List<QuestionCandidate>();
        for (var i = 0; i < n; i++)
        {
            var id = $"{prefix}{i}";
            var question = new StoredQuestion
            {
                QuestionId = Guid.NewGuid().ToString("N"), ScenarioId = Scenario, Date = "2026-10-31", ImageId = id,
                Form = QuestionForm.Single, Stream = LabelStream.Learn, Reason = reason, CreatedAt = "2026-10-31T00:00:00Z",
                AskedTo = "dev", State = QuestionState.Open,
            };
            await _questions.CreateAsync(question, default);
            await _questions.TryCloseAsync(Scenario, question.QuestionId, QuestionState.Answered, "OK", "dev", default);
            done.Add(P(id, 0.10 + 0.01 * i));
        }
        return done;
    }

    [Fact]
    public async Task PatchScores_AskTheNearestThePassMarkOnceRepresentativeOkLabelsSetIt()
    {
        // 10 장의 점수 0.10 ~ 0.19 → 문턱 = 95 백분위 = 0.1855.
        var answered = await AnsweredOkAsync(10, QuestionReason.RandomFloor);
        _source.Candidates = [.. answered, P("far", 0.90), P("near", 0.20), P("low", 0.05)];

        var q = await AskedAsync(Service(new QuestionsOptions { RandomFloor = 0 }));

        Assert.Equal("near", q.ImageId);
        Assert.Equal(QuestionReason.Uncertain, q.Reason);
        Assert.Equal(1.0 / (1.0 + Math.Abs(0.20 - 0.1855)), q.Score!.Value, 6);
    }

    [Fact]
    public async Task PatchScores_StartAtRandomUntilEnoughRepresentativeOkLabels()
    {
        var answered = await AnsweredOkAsync(QuestionThreshold.MinSamples - 1, QuestionReason.ColdStart);
        _source.Candidates = [.. answered, P("a", 0.50), P("b", 0.20)];

        var q = await AskedAsync(Service(new QuestionsOptions { RandomFloor = 0 }));

        Assert.Equal(QuestionReason.ColdStart, q.Reason);
        Assert.Null(q.Score);
    }

    [Fact]
    public async Task PatchScores_IgnoreOkLabelsCollectedByUncertainty_WhenSettingThePassMark()
    {
        // 어려운 정상이 몰린 `uncertain` 라벨은 문턱을 끌어올리므로 열 장이 있어도 대표 표본이 아니다.
        var answered = await AnsweredOkAsync(12, QuestionReason.Uncertain);
        _source.Candidates = [.. answered, P("a", 0.50), P("b", 0.20)];

        var q = await AskedAsync(Service(new QuestionsOptions { RandomFloor = 0 }));

        Assert.Equal(QuestionReason.ColdStart, q.Reason);
    }

    [Fact]
    public async Task PatchScores_TakePrecedenceOverTheGlobalMemorySignal()
    {
        var answered = await AnsweredOkAsync(10, QuestionReason.Random);
        // 전역 기억 단은 "g" 가 가장 헷갈린다고 하지만(5.0) 패치 판정자 기준으로는 "near" 가 문턱에 붙어 있다.
        _source.Candidates = [.. answered, new("g", "2026-10-31", 5.0, 0.90), new("near", "2026-10-31", 0.1, 0.19)];

        var q = await AskedAsync(Service(new QuestionsOptions { RandomFloor = 0 }));

        Assert.Equal("near", q.ImageId);
    }

    [Fact]
    public void Threshold_NeedsTheMinimumSampleAndInterpolatesThePercentile()
    {
        Assert.Null(QuestionThreshold.Calibrate([.. Enumerable.Repeat(0.1, QuestionThreshold.MinSamples - 1)]));
        var scores = Enumerable.Range(0, 11).Select(i => i / 10.0).ToArray(); // 0.0 .. 1.0
        Assert.Equal(0.95, QuestionThreshold.Calibrate(scores)!.Value, 9);
    }

    private sealed class FakeSource : IQuestionCandidateSource
    {
        public IReadOnlyList<QuestionCandidate> Candidates { get; set; } = [];
        public IReadOnlyList<string> LastDates { get; private set; } = [];

        public Task<IReadOnlyList<QuestionCandidate>> ListAsync(
            string scenarioId, IReadOnlyList<string> dates, CancellationToken cancellationToken)
        {
            LastDates = dates;
            return Task.FromResult(Candidates);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeScenarios(string? type) : IScenarioStore
    {
        public Task<Scenario?> GetAsync(string scenarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Scenario?>(scenarioId == Scenario
                ? new Scenario { ScenarioId = Scenario, Name = "demo", Criteria = "c", Type = type }
                : null);

        public Task<IReadOnlyList<Scenario>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Scenario?> CreateAsync(ScenarioInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Scenario?> UpdateAsync(string scenarioId, ScenarioInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string scenarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetNgLabelAsync(string scenarioId, string referenceId, string? label, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
