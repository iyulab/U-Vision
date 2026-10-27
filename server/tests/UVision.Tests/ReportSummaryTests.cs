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

    [Fact]
    public void Scenario_comes_from_the_report_header_then_the_file_name()
    {
        // ML 헤더 «목록 `x`» · VLM 헤더 «- 목록: `y`» 가 먼저.
        Assert.Equal("x", ReportSummary.Parse("2026-10-25-other-mloop.md", Ml)!.Scenario);
        Assert.Equal("y", ReportSummary.Parse("2026-10-25-other-qwen.md", Vlm.Replace("- 모드:", "- 목록: `y`\n- 모드:"))!.Scenario);
        // 헤더에 목록이 없는 옛 리포트는 `날짜-시나리오-판정원…` 이름에서.
        Assert.Equal("visa-pcb1-highshot", ReportSummary.Parse("2026-10-25-visa-pcb1-highshot-qwen-fewshot3-offset7.md", Vlm)!.Scenario);
        Assert.Null(ReportSummary.Parse("notes.md", Vlm)!.Scenario);
    }

    [Fact]
    public void Pivot_puts_judges_side_by_side_per_scenario_and_joins_repeats()
    {
        var table = ReportSummary.Render([
            ("2026-10-25-s1-qwen-zeroshot.md", Vlm),
            ("2026-10-25-s1-qwen-zeroshot-b.md", Vlm),
            ("2026-10-25-s1-mloop.md", Ml.Replace("`x`", "`s1`")),
            ("2026-10-25-s2-mloop.md", Ml.Replace("`x`", "`s2`")),
        ]);

        var pivot = table[table.IndexOf("판정원 × 시나리오", StringComparison.Ordinal)..];
        Assert.Contains("| s1 | 1/3 · 1/2 ; 1/3 · 1/2 | 2/3 · 0/3 |", pivot);
        Assert.Contains("| s2 |  | 2/3 · 0/3 |", pivot);
    }

    [Fact]
    public void Latency_p50_is_read_from_both_report_kinds()
    {
        var vlm = Vlm.Replace("- 이미지 수:", "- latency(동시 3): p50 **16228 ms**, p95 **92593 ms**\n- 이미지 수:");
        Assert.Equal(16228, ReportSummary.Parse("a.md", vlm)!.LatencyP50Ms);
        var ml = Ml.Replace("- 시험 6장", "- 지연: p50 121 ms · max 225 ms (직렬)\n- 시험 6장");
        Assert.Equal(121, ReportSummary.Parse("b.md", ml)!.LatencyP50Ms);
        Assert.Null(ReportSummary.Parse("c.md", Ml)!.LatencyP50Ms);
    }

    [Fact]
    public void Coverage_names_the_terms_per_scenario_and_flags_the_unknown()
    {
        var sources = ReportSummary.ParseSources("# 주석\nvisa-\tVisA 공개\tCC BY 4.0\nvisa-pcb4\tVisA pcb4 만\tCC BY 4.0 (pcb4)\n");
        var rows = new[]
        {
            ReportSummary.Parse("2026-10-25-visa-pcb1-qwen-fewshot3.md", Vlm)!,
            ReportSummary.Parse("2026-10-25-visa-pcb4-mloop.md", Ml.Replace("`x`", "`visa-pcb4`"))!,
            ReportSummary.Parse("2026-10-25-other-mloop.md", Ml)!,
        };

        var table = ReportSummary.RenderCoverage(rows, sources);

        Assert.Contains("`visa-pcb1` · VisA 공개 · CC BY 4.0", table);
        Assert.Contains("`visa-pcb4` · VisA pcb4 만 · CC BY 4.0 (pcb4)", table); // 가장 긴 접두어가 이긴다
        Assert.Contains("`x` · 데이터 미기재 · **이용 조건 미확인**", table);
    }
}

/// <summary>리포트 표 칸 — 실패 사유에 섞인 주소는 리포트에 남지 않는다.</summary>
public class ReportTextTests
{
    [Fact]
    public void Cell_RemovesUrls_Newlines_AndPipes_AndTruncates()
    {
        var cell = ReportText.Cell("HttpRequestException: Connection refused (https://gateway.example:8443/openai/v1/chat)\nline | two");

        Assert.DoesNotContain("example", cell);
        Assert.DoesNotContain('\n', cell);
        Assert.DoesNotContain('|', cell);
        Assert.Contains("<url>", cell);
        Assert.Equal(161, ReportText.Cell(new string('가', 400)).Length);
    }
}
