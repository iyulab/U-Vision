using UVision.Api.Services.Questions;
using Xunit;

namespace UVision.Tests;

public class QuestionSelectorTests
{
    private static QuestionCandidate C(string id, double? uncertainty) => new(id, "2026-10-31", uncertainty);

    private static Dictionary<string, string> Ann(string? distance, string? agreed)
    {
        var a = new Dictionary<string, string>();
        if (distance is not null) a["memory:nearest_distance"] = distance;
        if (agreed is not null) a["memory:agreed_class"] = agreed;
        return a;
    }

    [Fact]
    public void NoCandidates_PicksNothing() =>
        Assert.Null(QuestionSelector.Pick([], new QuestionPolicy(0.0), new Random(1)));

    [Fact]
    public void PicksTheMostUncertain_AndRecordsTheScoreAsTheReason()
    {
        var pick = QuestionSelector.Pick([C("a", 0.02), C("b", 1.04), C("c", 0.30)], new QuestionPolicy(0.0), new Random(1))!;

        Assert.Equal("b", pick.Candidate.ImageId);
        Assert.Equal(QuestionReason.Uncertain, pick.Reason);
        Assert.Equal(1.04, pick.Score);
    }

    [Fact]
    public void Ties_GoToTheFirstCandidate_SoTheSameInputGivesTheSameAnswer()
    {
        var pick = QuestionSelector.Pick([C("first", 0.5), C("second", 0.5)], new QuestionPolicy(0.0), new Random(7))!;
        Assert.Equal("first", pick.Candidate.ImageId);
    }

    [Fact]
    public void WithoutAnyMemorySignal_StartsAtRandom_WithNoScore()
    {
        var pick = QuestionSelector.Pick([C("a", null), C("b", null), C("c", null)], new QuestionPolicy(0.0), new Random(3))!;

        Assert.Equal(QuestionReason.ColdStart, pick.Reason);
        Assert.Null(pick.Score);
    }

    [Fact]
    public void RandomFloorOfOne_AlwaysPicksAtRandom_AndOfZero_NeverDoes()
    {
        var pool = new[] { C("a", 0.9), C("b", 0.1), C("c", 0.2), C("d", null) };

        var always = Enumerable.Range(0, 50).Select(i => QuestionSelector.Pick(pool, new QuestionPolicy(1.0), new Random(i))!).ToList();
        var never = Enumerable.Range(0, 50).Select(i => QuestionSelector.Pick(pool, new QuestionPolicy(0.0), new Random(i))!).ToList();

        Assert.All(always, p => { Assert.Equal(QuestionReason.RandomFloor, p.Reason); Assert.Null(p.Score); });
        Assert.All(never, p => Assert.Equal("a", p.Candidate.ImageId));
        Assert.True(always.Select(p => p.Candidate.ImageId).Distinct().Count() > 1); // 한 사진에 몰리지 않는다
    }

    [Fact]
    public void RandomFloor_MixesInAboutThatShareOfRandomPicks()
    {
        var pool = new[] { C("a", 0.9), C("b", 0.1), C("c", 0.2) };
        var rng = new Random(11);
        var picks = Enumerable.Range(0, 2000).Select(_ => QuestionSelector.Pick(pool, new QuestionPolicy(0.25), rng)!).ToList();

        var share = picks.Count(p => p.Reason == QuestionReason.RandomFloor) / 2000.0;
        Assert.InRange(share, 0.20, 0.30);
    }

    [Fact]
    public void Signals_RankDisagreementAboveEveryDistance_AndFartherAboveNearer()
    {
        var disagree = QuestionSignals.UncertaintyFromAnnotations(Ann("0.04", agreed: null));   // 이웃이 갈림
        var far = QuestionSignals.UncertaintyFromAnnotations(Ann("0.40", agreed: "OK"));        // 문턱 밖이지만 한 클래스
        var near = QuestionSignals.UncertaintyFromAnnotations(Ann("0.01", agreed: "OK"));

        Assert.True(disagree > far);
        Assert.True(far > near);
        Assert.Equal(1.04, disagree);
    }

    [Fact]
    public void Signals_AreNullWhenTheMemoryLeftNoDistance()
    {
        Assert.Null(QuestionSignals.UncertaintyFromAnnotations(null));
        Assert.Null(QuestionSignals.UncertaintyFromAnnotations(Ann(null, agreed: null)));
        Assert.Null(QuestionSignals.UncertaintyFromAnnotations(new Dictionary<string, string> { ["memory:nearest_distance"] = "x" }));
    }
}
