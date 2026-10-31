using System.Globalization;
using HoneAI;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;

namespace UVision.Api.Services.Questions;

/// <summary>문제 후보를 모으는 곳 — 지금은 결정 장부. 시험에서는 바꿔 끼운다.</summary>
public interface IQuestionCandidateSource
{
    /// <summary>주어진 날짜들에서 판정된 사진을 후보로 돌려준다(라벨 · 이미 물은 것을 거르는 일은 부르는 쪽 몫).</summary>
    Task<IReadOnlyList<QuestionCandidate>> ListAsync(
        string scenarioId, IReadOnlyList<string> dates, CancellationToken cancellationToken);
}

/// <summary>
/// 결정 장부(<c>{scenario}/ledger/{date}.jsonl</c>)에서 후보를 모은다. 불확실도는 판정 때 기억 단이 남긴 신호의 근사다
/// (<see cref="QuestionSignals"/>) — 기억이 자란 뒤에는 낡을 수 있고, 새 임베딩을 부르지 않는 대신 그렇다.
/// </summary>
public sealed class LedgerQuestionCandidateSource : IQuestionCandidateSource
{
    private readonly StoragePaths _paths;

    public LedgerQuestionCandidateSource(StoragePaths paths) => _paths = paths;

    public async Task<IReadOnlyList<QuestionCandidate>> ListAsync(
        string scenarioId, IReadOnlyList<string> dates, CancellationToken cancellationToken)
    {
        var candidates = new List<QuestionCandidate>();
        foreach (var date in dates)
        {
            var ledger = _paths.LedgerJsonl(scenarioId, date);
            if (!File.Exists(ledger))
                continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var record in new JsonlProvenanceSink(ledger).ReadAsync(cancellationToken: cancellationToken))
            {
                if (!seen.Add(record.SubjectId))
                    continue;
                var annotations = record.Provenance.Annotations;
                candidates.Add(new QuestionCandidate(
                    record.SubjectId, date, QuestionSignals.UncertaintyFromAnnotations(annotations),
                    QuestionSignals.PatchScoreFromAnnotations(annotations)));
            }
        }
        return candidates;
    }
}

/// <summary>문제를 내는 일의 결과 — HTTP 를 모른다.</summary>
public abstract record QuestionOutcome
{
    public sealed record ScenarioMissing : QuestionOutcome;

    /// <summary>이 시나리오 유형은 단일 판정 문제를 낼 수 없다(오투입 확인은 기대 품번 상대라 사진 하나로 묻지 못한다).</summary>
    public sealed record Unsupported(string Reason) : QuestionOutcome;

    public sealed record NothingToAsk : QuestionOutcome;

    public sealed record Asked(StoredQuestion Question, IReadOnlyList<string> Choices) : QuestionOutcome;
}

/// <summary>답을 기록한 일의 결과.</summary>
public abstract record AnswerOutcome
{
    public sealed record ScenarioMissing : AnswerOutcome;

    public sealed record QuestionMissing : AnswerOutcome;

    /// <summary>이미 답했거나 거둬들인 문제.</summary>
    public sealed record AlreadyClosed : AnswerOutcome;

    /// <summary>그 사이 다른 경로로 사진에 라벨이 붙어 라벨을 덮어쓰지 않고 문제를 거둬들였다.</summary>
    public sealed record AlreadyLabeled : AnswerOutcome;

    public sealed record InvalidChoice(string Detail) : AnswerOutcome;

    /// <summary><paramref name="Kind"/> 는 <c>label</c> 또는 <c>abstain</c>.</summary>
    public sealed record Recorded(string QuestionId, string Kind) : AnswerOutcome;
}

/// <summary>
/// 학습용 문제 큐 — 후보를 모아 하나 고르고(<see cref="QuestionSelector"/>) 문제로 기록하고, 답을 라벨(학습용 표식) 또는 기권으로 적는다.
/// 한 사진은 한 번만 묻는다(기권해도 다시 묻지 않는다). 같은 풀이자가 답하지 않은 문제가 있으면 새로 내지 않고 그것을 다시 보여 준다.
/// </summary>
public sealed class QuestionService
{
    private readonly IScenarioStore _scenarios;
    private readonly ILabelStore _labels;
    private readonly IQuestionStore _questions;
    private readonly IQuestionCandidateSource _candidates;
    private readonly QuestionsOptions _options;
    private readonly TimeProvider _clock;
    private readonly Random _rng;

    // 문제를 내는 일과 답을 적는 일을 한 줄로 세운다 — 두 풀이자가 같은 사진을 받거나 같은 문제에 두 번 답하지 않게.
    // 저장소는 한 프로세스가 연다(라벨 저장소와 같은 전제).
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QuestionService(
        IScenarioStore scenarios, ILabelStore labels, IQuestionStore questions, IQuestionCandidateSource candidates,
        QuestionsOptions options, TimeProvider? clock = null, Random? rng = null)
    {
        _scenarios = scenarios;
        _labels = labels;
        _questions = questions;
        _candidates = candidates;
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _rng = rng ?? Random.Shared;
    }

    public async Task<QuestionOutcome> NextAsync(string scenarioId, string by, CancellationToken cancellationToken)
    {
        var scenario = await _scenarios.GetAsync(scenarioId, cancellationToken);
        if (scenario is null)
            return new QuestionOutcome.ScenarioMissing();
        var effective = EffectiveScenario.Resolve(scenario, mlEnabled: false);
        if (effective.IsVerification)
            return new QuestionOutcome.Unsupported("오투입 확인 시나리오는 기대 품번 상대로 판정하므로 사진 하나로 묻는 문제를 내지 않는다");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var asked = await _questions.ListAsync(scenarioId, cancellationToken);
            // 풀이자가 이미 받은 문제가 열려 있으면 그것을 다시 보여 준다(새로고침 · 앱 재시작으로 문제를 건너뛰지 않게).
            var open = asked.Where(q => q.State == QuestionState.Open && q.AskedTo == by)
                .OrderBy(q => q.CreatedAt, StringComparer.Ordinal).FirstOrDefault();
            if (open is not null)
                return new QuestionOutcome.Asked(open, effective.Labels);

            var dates = WindowDates();
            var candidates = await _candidates.ListAsync(scenarioId, dates, cancellationToken);
            var skip = new HashSet<(string Date, string Image)>(asked.Select(q => (q.Date, q.ImageId)));
            foreach (var date in dates)
            {
                foreach (var l in await _labels.ListAsync(scenarioId, date, cancellationToken))
                    if (l.OperativeLabel is not null || l.AbstainCount > 0)
                        skip.Add((date, l.ImageId));
            }
            var pool = candidates.Where(c => !skip.Contains((c.Date, c.ImageId))).ToList();
            if (pool.Count == 0)
                return new QuestionOutcome.NothingToAsk();
            pool = WithOperatingJudgeUncertainty(pool, candidates, asked);

            var pick = Pick(pool);
            var question = new StoredQuestion
            {
                QuestionId = Guid.NewGuid().ToString("N"),
                ScenarioId = scenarioId,
                Date = pick.Candidate.Date,
                ImageId = pick.Candidate.ImageId,
                Form = QuestionForm.Single,
                Stream = LabelStream.Learn,
                Reason = pick.Reason,
                Score = pick.Score,
                CreatedAt = _clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                AskedTo = by,
                State = QuestionState.Open,
            };
            await _questions.CreateAsync(question, cancellationToken);
            return new QuestionOutcome.Asked(question, effective.Labels);
        }
        finally { _gate.Release(); }
    }

    public async Task<AnswerOutcome> AnswerAsync(
        string scenarioId, string questionId, string? choice, bool abstain, string by, CancellationToken cancellationToken)
    {
        if (abstain == (choice is not null))
            return new AnswerOutcome.InvalidChoice("choice 와 abstain 중 정확히 하나만 주어야 한다");

        var scenario = await _scenarios.GetAsync(scenarioId, cancellationToken);
        if (scenario is null)
            return new AnswerOutcome.ScenarioMissing();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var question = await _questions.ReadAsync(scenarioId, questionId, cancellationToken);
            if (question is null)
                return new AnswerOutcome.QuestionMissing();
            if (question.State != QuestionState.Open)
                return new AnswerOutcome.AlreadyClosed();

            if (!abstain)
            {
                var allowed = EffectiveScenario.Resolve(scenario, mlEnabled: false).Labels;
                if (!LabelSet.IsValid(choice!, allowed))
                    return new AnswerOutcome.InvalidChoice($"허용되지 않은 라벨: '{choice}'");
            }

            // 그 사이 다른 경로(결과 목록의 라벨 달기)로 라벨이 붙었으면 덮어쓰지 않는다.
            var existing = await _labels.ReadAsync(scenarioId, question.Date, question.ImageId, cancellationToken);
            if (existing?.OperativeLabel is not null)
            {
                await _questions.TryCloseAsync(scenarioId, questionId, QuestionState.Withdrawn, null, by, cancellationToken);
                return new AnswerOutcome.AlreadyLabeled();
            }

            if (abstain)
            {
                await _labels.AppendAbstainAsync(
                    scenarioId, question.Date, question.ImageId, by, cancellationToken, question.Stream);
                await _questions.TryCloseAsync(scenarioId, questionId, QuestionState.Abstained, null, by, cancellationToken);
                return new AnswerOutcome.Recorded(questionId, "abstain");
            }

            await _labels.AppendLabelAsync(
                scenarioId, question.Date, question.ImageId, choice!, by, cancellationToken, question.Stream);
            await _questions.TryCloseAsync(scenarioId, questionId, QuestionState.Answered, choice, by, cancellationToken);
            return new AnswerOutcome.Recorded(questionId, "label");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 불확실도는 켜진 판정자 중 가장 강한 것의 것이다. 후보에 패치 점수가 있으면 패치 판정자 기준 — 점수가 대표 표본의 정상 라벨로
    /// 잡은 문턱과 가까울수록 불확실하다. 문턱이 아직 없으면(대표 정상 라벨이 모자람) 신호 없음 = 무작위 시작이고, 전역 기억 단의
    /// 불확실도로 대신하지 않는다(패치 판정자에는 무작위보다 나쁘다). 패치 점수가 하나도 없으면 전역 기억 단 신호 그대로.
    /// </summary>
    private static List<QuestionCandidate> WithOperatingJudgeUncertainty(
        List<QuestionCandidate> pool, IReadOnlyList<QuestionCandidate> allInWindow, IReadOnlyList<StoredQuestion> asked)
    {
        if (!pool.Any(c => c.PatchScore is not null))
            return pool;

        var scoreOf = allInWindow.Where(c => c.PatchScore is not null)
            .ToDictionary(c => (c.Date, c.ImageId), c => c.PatchScore!.Value);
        var representativeOk = new List<double>();
        foreach (var q in asked)
        {
            if (q.State == QuestionState.Answered && q.Answer == ScenarioVocabulary.Ok
                && QuestionThreshold.IsRepresentative(q.Reason) && scoreOf.TryGetValue((q.Date, q.ImageId), out var score))
                representativeOk.Add(score);
        }
        var threshold = QuestionThreshold.Calibrate(representativeOk);

        return pool.Select(c => c with
        {
            Uncertainty = c.PatchScore is { } s && threshold is { } t ? QuestionSignals.UncertaintyFromPatchScore(s, t) : null,
        }).ToList();
    }

    private QuestionPick Pick(IReadOnlyList<QuestionCandidate> pool) =>
        _options.Rule == QuestionsOptions.UncertainRule
            ? QuestionSelector.Pick(pool, new QuestionPolicy(_options.RandomFloor), _rng)!
            : new QuestionPick(pool[_rng.Next(pool.Count)], QuestionReason.Random, null);

    /// <summary>오늘부터 거슬러 올라가는 후보 날짜 창(UTC 날짜 버킷, 결과의 날짜 버킷과 같다).</summary>
    private List<string> WindowDates()
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        return Enumerable.Range(0, _options.WindowDays)
            .Select(i => today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();
    }
}
