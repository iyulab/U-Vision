using UVision.Api.Services.Counting;
using Xunit;

namespace UVision.Tests;

/// <summary>구성 확인 — 종류별 기대와 센 결과의 대조(항목 · 판정 · 과잉 정책 · 흔들림).</summary>
public class CompositionEvaluatorTests
{
    private static Detection D(string cls, double confidence) => new(cls, 0.1, 0.1, 0.1, 0.1, confidence);

    private static Dictionary<string, int> Expect(params (string Kind, int Count)[] entries) =>
        entries.ToDictionary(e => e.Kind, e => e.Count, StringComparer.Ordinal);

    [Fact]
    public void AllKindsAsExpected_IsRight_WithEveryItemOk()
    {
        var detections = new[] { D("scalpel", 0.9), D("scalpel", 0.9), D("forceps", 0.9) };

        var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("scalpel", 2), ("forceps", 1)));

        Assert.True(r.WithinRange);
        Assert.False(r.Uncertain);
        Assert.Equal(3, r.Expected);
        Assert.Equal(0, r.Tolerance);
        Assert.All(r.Items!, i => Assert.Equal(ItemStatus.Ok, i.Status));
        Assert.Equal(new[] { "forceps", "scalpel" }, r.Items!.Select(i => i.Class));
    }

    [Fact]
    public void AMissingKind_IsWrong_AndListedAsMissing()
    {
        var detections = new[] { D("scalpel", 0.9), D("scalpel", 0.9) };

        var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("scalpel", 2), ("forceps", 1)));

        Assert.False(r.WithinRange);
        Assert.Contains(new CountItem("forceps", 1, 0, ItemStatus.Missing), r.Items!);
        Assert.Contains(new CountItem("scalpel", 2, 2, ItemStatus.Ok), r.Items!);
    }

    [Fact]
    public void FewerThanExpected_IsMissing_EvenWhenTheExtraPolicyIsLenient()
    {
        var detections = new[] { D("gauze", 0.9), D("gauze", 0.9), D("gauze", 0.9) };

        foreach (var policy in ExtraPolicies.All)
        {
            var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("gauze", 4)), policy);

            Assert.False(r.WithinRange);
            Assert.Equal(ItemStatus.Missing, Assert.Single(r.Items!).Status);
        }
    }

    [Theory]
    [InlineData(ExtraPolicies.Ng, false, ItemStatus.Extra)]
    [InlineData(ExtraPolicies.Warn, true, ItemStatus.Extra)]
    [InlineData(ExtraPolicies.Ignore, true, ItemStatus.Ok)]
    public void MoreThanExpected_FollowsTheExtraPolicy(string policy, bool right, string status)
    {
        var detections = new[] { D("gauze", 0.9), D("gauze", 0.9), D("gauze", 0.9) };

        var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("gauze", 2)), policy);

        Assert.Equal(right, r.WithinRange);
        var item = Assert.Single(r.Items!);
        Assert.Equal(new CountItem("gauze", 2, 3, status), item);
    }

    [Theory]
    [InlineData(ExtraPolicies.Ng, false, true)]
    [InlineData(ExtraPolicies.Warn, true, true)]
    [InlineData(ExtraPolicies.Ignore, true, false)]
    public void AKindNobodyExpected_IsAnExtraOfExpectedZero_UnlessIgnored(string policy, bool right, bool listed)
    {
        var detections = new[] { D("scalpel", 0.9), D("lighter", 0.8) };

        var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("scalpel", 1)), policy);

        Assert.Equal(right, r.WithinRange);
        Assert.Equal(listed, r.Items!.Any(i => i == new CountItem("lighter", 0, 1, ItemStatus.Extra)));
        Assert.Equal(1, r.ByClass["lighter"]); // 센 수는 정책과 상관없이 그대로 남는다
    }

    [Fact]
    public void BoxesBelowTheThreshold_AreNotCounted_ButStayAsNearBoxes()
    {
        var detections = new[] { D("scalpel", 0.9), D("forceps", 0.45) };

        var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("scalpel", 1), ("forceps", 1)));

        Assert.False(r.WithinRange);
        Assert.Equal(ItemStatus.Missing, r.Items!.Single(i => i.Class == "forceps").Status);
        Assert.Equal("forceps", Assert.Single(r.NearBoxes).Class);
    }

    [Fact]
    public void OnlyTheNamedCountClasses_AreCounted()
    {
        var detections = new[] { D("scalpel", 0.9), D("lighter", 0.9) };

        var r = CompositionEvaluator.Evaluate(detections, 0.5, ["scalpel"], Expect(("scalpel", 1)));

        Assert.True(r.WithinRange); // 세지 않는 종류는 과잉으로도 잡히지 않는다
        Assert.DoesNotContain(r.Items!, i => i.Class == "lighter");
    }

    [Fact]
    public void ABoxNearTheThreshold_ThatTurnsMissingIntoOk_MakesItUncertain()
    {
        // 기대 {forceps: 1}. 문턱 바로 위(0.52) 한 개뿐 → 지금은 맞다. 그 한 개가 없으면 누락.
        var detections = new[] { D("forceps", 0.52) };

        var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("forceps", 1)));

        Assert.True(r.WithinRange);
        Assert.True(r.Uncertain);
    }

    [Fact]
    public void ABoxNearTheThreshold_ThatTurnsOkIntoExtra_MakesItUncertain_OnlyWhenExtrasAreBad()
    {
        var detections = new[] { D("gauze", 0.9), D("gauze", 0.9), D("gauze", 0.52) };

        var strict = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("gauze", 2)), ExtraPolicies.Ng);
        var warn = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("gauze", 2)), ExtraPolicies.Warn);

        Assert.False(strict.WithinRange); // 센 3 > 기대 2
        Assert.True(strict.Uncertain);    // 0.52 가 없으면 2 = 맞다
        Assert.True(warn.WithinRange);
        Assert.False(warn.Uncertain);     // 과잉을 불량으로 보지 않으면 어느 쪽이든 맞다
    }

    [Fact]
    public void AnotherKindThatIsDefinitelyMissing_SettlesTheVerdict_SoNearBoxesDoNotMakeItUncertain()
    {
        var detections = new[] { D("scalpel", 0.52) }; // forceps 는 확실히 없다

        var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("scalpel", 1), ("forceps", 1)));

        Assert.False(r.WithinRange);
        Assert.False(r.Uncertain); // 어느 쪽으로 굴러도 forceps 누락 → 언제나 틀림
    }

    [Fact]
    public void EmptyDetections_EveryExpectedKindIsMissing()
    {
        var r = CompositionEvaluator.Evaluate([], 0.5, null, Expect(("scalpel", 2), ("forceps", 1)));

        Assert.False(r.WithinRange);
        Assert.False(r.Uncertain);
        Assert.Equal(0, r.Total);
        Assert.All(r.Items!, i => Assert.Equal(ItemStatus.Missing, i.Status));
    }

    [Fact]
    public void ItemsAreDeterministic_ExpectedKindsFirstThenUnexpected_EachOrdinal()
    {
        var detections = new[] { D("zeta", 0.9), D("alpha", 0.9), D("scalpel", 0.9), D("forceps", 0.9) };

        var r = CompositionEvaluator.Evaluate(detections, 0.5, null, Expect(("scalpel", 1), ("forceps", 1)));

        Assert.Equal(new[] { "forceps", "scalpel", "alpha", "zeta" }, r.Items!.Select(i => i.Class));
    }
}
