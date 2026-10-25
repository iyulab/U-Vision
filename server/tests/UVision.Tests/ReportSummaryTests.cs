extern alias bench;

using bench::UVision.Benchmark;
using Xunit;

namespace UVision.Tests;

/// <summary>리포트 → 판정원 비교표 — 표의 수치는 리포트에서만 나오고, 판정 불가는 못 잡음으로 센다.</summary>
public class ReportSummaryTests
{
    private const string Vlm = """
        ﻿# VLM 벤치마크 리포트

        - provider: `openai-compatible` / model: `m-1`
        - 모드: **few-shot** (기준 이미지 6장)
        - 이미지 수: 5 — 판정 4 (라벨됨 4) · **실패 1**

        ## 건별 결과

        | 이미지 | 정답 | 판정 | confidence | latency(ms) | 근거 |
        |---|---|---|---|---|---|
        | a/1.jpg | NG | NG | 0.90 | 10 | 핀 변형 |
        | a/2.jpg | NG | OK | 0.60 | 10 | 이상 없음 |
        | a/3.jpg | NG | **실패** | 0.00 | 10 | 시간 초과 |
        | a/4.jpg | OK | OK | 0.90 | 10 | 정상 |
        | a/5.jpg | OK | NG | 0.70 | 10 | 오경보 / 의심 |
        """;

    private const string Ml = """
        # ML 분류 정확도 — 목록 `x` test 분할

        - 시험 6장 — 분류 6 · 실패 0

        | 정답 \ 예측 | NG | OK |
        |---|---|---|
        | NG | 2 | 1 |
        | OK | 0 | 3 |
        """;

    [Fact]
    public void Vlm_CountsUnjudgedDefectsAsMissed()
    {
        var row = ReportSummary.Parse("2026-10-25-x-fewshot3.md", Vlm);

        Assert.NotNull(row);
        Assert.Equal("VLM few-shot (기준 이미지 6장) · m-1", row.Judge);
        Assert.Equal((3, 1, 2, 1, 1), (row.NgTotal, row.NgCaught, row.OkTotal, row.FalseAlarms, row.Unjudged));
    }

    [Fact]
    public void Ml_ReadsTheConfusionMatrix()
    {
        var row = ReportSummary.Parse("ml.md", Ml);

        Assert.NotNull(row);
        Assert.Equal((3, 2, 3, 0, 0), (row.NgTotal, row.NgCaught, row.OkTotal, row.FalseAlarms, row.Unjudged));
    }

    [Fact]
    public void Render_ListsEachReport_AndSkipsOtherDocuments()
    {
        var table = ReportSummary.Render([("b.md", Ml), ("a.md", Vlm), ("c.md", "# 메모\n\n내용")]);

        Assert.Contains("| a | VLM few-shot (기준 이미지 6장) · m-1 | 1/3 | 1/2 | 1 |", table);
        Assert.Contains("| b | 전용 ML | 2/3 | 0/3 | 0 |", table);
        Assert.DoesNotContain("| c |", table);
        Assert.True(table.IndexOf("| a |", StringComparison.Ordinal) < table.IndexOf("| b |", StringComparison.Ordinal));
    }
}
