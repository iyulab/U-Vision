extern alias bench;

using bench::UVision.Benchmark;
using UVision.Api.Services.Counting;
using Xunit;

namespace UVision.Tests;

/// <summary>구성 확인 오차 측정의 순수 부분 — 정답 항목 · 문턱별 채점 · 놓침 · 입력 검증. 합성 상자로만 돈다(실 검출기 숫자가 아니다).</summary>
public class CompositionErrorBenchTests
{
    private static Detection D(string cls, double confidence) => new(cls, 0, 0, 0, 0, confidence);

    private static Dictionary<string, int> M(params (string Kind, int Count)[] entries) =>
        entries.ToDictionary(e => e.Kind, e => e.Count, StringComparer.Ordinal);

    private static CompositionErrorBench.CompositionCase Case(
        string id, Dictionary<string, int> expected, Dictionary<string, int> truth, params Detection[] boxes) =>
        new(id, expected, truth, boxes);

    private static readonly Dictionary<string, int> Tray = M(("scalpel", 2), ("forceps", 1));

    [Fact]
    public void APerfectDetector_ScoresPerfectly_OnEveryKindOfPhoto()
    {
        IReadOnlyList<CompositionErrorBench.CompositionCase> cases =
        [
            Case("ok", Tray, M(("scalpel", 2), ("forceps", 1)), D("scalpel", 0.9), D("scalpel", 0.9), D("forceps", 0.9)),
            Case("missing", Tray, M(("scalpel", 2)), D("scalpel", 0.9), D("scalpel", 0.9)),
            Case("extra", Tray, M(("scalpel", 3), ("forceps", 1)), D("scalpel", 0.9), D("scalpel", 0.9), D("scalpel", 0.9), D("forceps", 0.9)),
        ];

        var row = CompositionErrorBench.Evaluate(cases, [0.5]).Rows.Single();

        Assert.Equal(new CompositionErrorBench.Ratio(3, 3), row.ListMatch);
        Assert.Equal(new CompositionErrorBench.Ratio(1, 1), row.MissingRecall);
        Assert.Equal(new CompositionErrorBench.Ratio(1, 1), row.MissingPrecision);
        Assert.Equal(new CompositionErrorBench.Ratio(1, 1), row.ExtraRecall);
        Assert.Equal(new CompositionErrorBench.Ratio(3, 3), row.VerdictMatch);
        Assert.Equal(0, row.Escapes);
        Assert.Equal(0, row.FalseAlarms);
    }

    [Fact]
    public void AMissedMissingKind_IsAnEscape_AndLowersMissingRecall()
    {
        // 정답은 forceps 없음(구성 틀림). 검출기가 forceps 를 하나 지어내(0.9) 구성이 맞아 보인다 → NG 를 OK 로 통과시킴.
        var cases = new[] { Case("ghost", Tray, M(("scalpel", 2)), D("scalpel", 0.9), D("scalpel", 0.9), D("forceps", 0.9)) };

        var row = CompositionErrorBench.Evaluate(cases, [0.5]).Rows.Single();

        Assert.Equal(1, row.Escapes);
        Assert.Equal(0, row.FalseAlarms);
        Assert.Equal(new CompositionErrorBench.Ratio(0, 1), row.MissingRecall);
        Assert.Equal(new CompositionErrorBench.Ratio(0, 1), row.VerdictMatch);
        Assert.Equal(new CompositionErrorBench.Ratio(0, 1), row.ListMatch);
    }

    [Fact]
    public void ABlockedGoodTray_IsAFalseAlarm_AndAFalseMissingLowersMissingPrecision()
    {
        // 정답은 구성이 맞다. 검출기가 forceps 를 놓친다(상자 없음) → NG 로 막고 가짜 누락.
        var cases = new[] { Case("blocked", Tray, M(("scalpel", 2), ("forceps", 1)), D("scalpel", 0.9), D("scalpel", 0.9)) };

        var row = CompositionErrorBench.Evaluate(cases, [0.5]).Rows.Single();

        Assert.Equal(1, row.FalseAlarms);
        Assert.Equal(0, row.Escapes);
        Assert.Equal(new CompositionErrorBench.Ratio(0, 0), row.MissingRecall);      // 참 누락이 없어 비율은 정의되지 않는다
        Assert.Equal(new CompositionErrorBench.Ratio(0, 1), row.MissingPrecision);   // 잡은 누락 하나가 가짜
        Assert.Null(row.MissingRecall.Rate);
    }

    [Fact]
    public void ABoxNearTheThreshold_IsCountedAsUncertain_AndCountsAsABlockedPhoto()
    {
        // 정답은 구성이 맞다. forceps 가 문턱 바로 위(0.52) 한 개 — 지금은 맞지만 흔들려 운영에서는 NG(사람 확인)로 간다.
        var cases = new[] { Case("shaky", Tray, M(("scalpel", 2), ("forceps", 1)), D("scalpel", 0.9), D("scalpel", 0.9), D("forceps", 0.52)) };

        var row = CompositionErrorBench.Evaluate(cases, [0.5]).Rows.Single();

        Assert.Equal(new CompositionErrorBench.Ratio(1, 1), row.Uncertain);
        Assert.Equal(1, row.FalseAlarms); // 사람 확인 대상이라 OK 로 통과시키지 않는다 — 정답이 OK 이므로 오경보로 센다
    }

    [Fact]
    public void RaisingTheThreshold_TurnsAWeakBoxIntoAMiss()
    {
        var cases = new[] { Case("weak", Tray, M(("scalpel", 2), ("forceps", 1)), D("scalpel", 0.95), D("scalpel", 0.95), D("forceps", 0.6)) };

        var rows = CompositionErrorBench.Evaluate(cases, [0.3, 0.8]).Rows;

        Assert.Equal(0, rows[0].FalseAlarms); // 0.3: 확실히 센다
        Assert.Equal(1, rows[1].FalseAlarms); // 0.8: forceps 0.6 이 빠져 누락
    }

    [Fact]
    public void TheExtraPolicy_ChangesTheTruthAndThePrediction_ByTheSameFunction()
    {
        var cases = new[] { Case("spare", Tray, M(("scalpel", 3), ("forceps", 1)), D("scalpel", 0.9), D("scalpel", 0.9), D("scalpel", 0.9), D("forceps", 0.9)) };

        var strict = CompositionErrorBench.Evaluate(cases, [0.5], ExtraPolicies.Ng).Rows.Single();
        var warn = CompositionErrorBench.Evaluate(cases, [0.5], ExtraPolicies.Warn).Rows.Single();

        Assert.Equal(new CompositionErrorBench.Ratio(1, 1), strict.ExtraRecall); // 과잉이 불량이라 잡힘
        Assert.Equal(new CompositionErrorBench.Ratio(1, 1), warn.ExtraRecall);   // warn 도 항목에는 올린다
        Assert.Equal(new CompositionErrorBench.Ratio(1, 1), warn.VerdictMatch);  // 정답도 예측도 OK
    }

    [Fact]
    public void Parse_ReadsCases_AndRejectsInputsThatWouldMisScore()
    {
        var cases = CompositionErrorBench.Parse(
            """[{"id":"a","expected":{"helmet":2},"truth":{"helmet":1},"detections":[{"class":"helmet","confidence":0.9}]}]""");

        var c = Assert.Single(cases);
        Assert.Equal("a", c.Id);
        Assert.Equal(2, c.Expected["helmet"]);
        Assert.Equal(1, c.Truth["helmet"]);

        Assert.Throws<InvalidDataException>(() => CompositionErrorBench.Parse("""[{"truth":{},"detections":[]}]"""));
        Assert.Throws<InvalidDataException>(() => CompositionErrorBench.Parse("""[{"expected":{"a":-1},"truth":{},"detections":[]}]"""));
        Assert.Throws<InvalidDataException>(() => CompositionErrorBench.Parse("""[{"expected":{"a":1},"detections":[]}]"""));
        Assert.Throws<InvalidDataException>(() => CompositionErrorBench.Parse("""[{"expected":{"a":1},"truth":{}}]"""));
        Assert.Throws<InvalidDataException>(() =>
            CompositionErrorBench.Parse("""[{"expected":{"a":1},"truth":{},"detections":[{"class":"a","confidence":1.5}]}]"""));
    }

    [Fact]
    public void Markdown_CarriesTheHonestyNote_AndTheEscapeColumn()
    {
        var cases = new[] { Case("a", Tray, M(("scalpel", 2), ("forceps", 1)), D("scalpel", 0.9), D("scalpel", 0.9), D("forceps", 0.9)) };

        var md = CompositionErrorBench.ToMarkdown(CompositionErrorBench.Evaluate(cases, [0.5]));

        Assert.Contains("입력한 상자의 숫자이지 검출기 일반의 숫자가 아니다", md);
        Assert.Contains("놓침(NG→OK)", md);
        Assert.Contains("(1/1)", md); // 비율마다 분모를 함께 적는다
    }
}
