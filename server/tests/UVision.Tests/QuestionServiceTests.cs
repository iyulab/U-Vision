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

        var q = await AskedAsync(Service());

        Assert.Contains(q.ImageId, new[] { "a", "b", "c" });
        Assert.Equal(QuestionReason.Random, q.Reason);
        Assert.Null(q.Score);
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
    public void Options_RejectUnknownRulesAndOutOfRangeValues()
    {
        Assert.Throws<ArgumentException>(() => new QuestionsOptions { Rule = "magic" }.Validate());
        Assert.Throws<ArgumentException>(() => new QuestionsOptions { RandomFloor = 1.5 }.Validate());
        Assert.Throws<ArgumentException>(() => new QuestionsOptions { RandomFloor = double.NaN }.Validate());
        Assert.Throws<ArgumentException>(() => new QuestionsOptions { WindowDays = 0 }.Validate());
        new QuestionsOptions().Validate(); // 기본값은 유효하다
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
