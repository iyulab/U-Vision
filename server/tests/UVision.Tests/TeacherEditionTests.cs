using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 교사 판 — 프롬프트 · 기준 이미지 · 추론 설정이 바뀌면 판이 바뀌고, 답을 바꾸지 않는 것(기대 품번 · 시간 한도)은
/// 판을 가르지 않는다. 관문 0 이 «어느 교사가 맞혔는가»를 판 단위로 세는 전제.
/// </summary>
public class TeacherEditionTests
{
    private const string Model = "openai-compatible:qwen";

    private static ScenarioContext Scenario(string criteria = "스크래치 없음", string? expectedPart = null) =>
        new() { ScenarioId = "demo", Criteria = criteria, ExpectedPart = expectedPart };

    private static ReferenceImage Ref(byte seed, ReferenceLabel label = ReferenceLabel.Ok, string? ng = null) =>
        new() { Data = new byte[] { 0xFF, 0xD8, seed, 1, 2, 3 }, Label = label, NgLabel = ng };

    private static string Edition(
        ScenarioContext? scenario = null, VlmOptions? options = null, int maxDim = 0,
        params ReferenceImage[] refs) =>
        TeacherEdition.Of(Model, options ?? new VlmOptions(), scenario ?? Scenario(), maxDim, refs);

    [Fact]
    public void Shape_IsModelThenPromptThenReferences()
    {
        var edition = Edition();
        Assert.Matches(@"^openai-compatible:qwen\|p:[0-9a-f]{8}\|r:none$", edition);
        Assert.Matches(@"\|r:[0-9a-f]{8}$", Edition(refs: Ref(1)));
    }

    [Fact]
    public void SameInputs_SameEdition()
    {
        Assert.Equal(Edition(refs: [Ref(1), Ref(2)]), Edition(refs: [Ref(1), Ref(2)]));
    }

    [Fact]
    public void Criteria_ChangesPromptHash()
    {
        Assert.NotEqual(Edition(), Edition(Scenario("스크래치 · 변색 없음")));
        Assert.Equal(PartOf(Edition(), "r"), PartOf(Edition(Scenario("스크래치 · 변색 없음")), "r"));
    }

    [Fact]
    public void DefectClasses_ChangePromptHash()
    {
        var with = Scenario() with { DefectClasses = [new ScenarioClass { Id = "scratch" }] };
        Assert.NotEqual(Edition(), Edition(with));
    }

    [Fact]
    public void ReferenceContent_LabelAndOrder_ChangeReferenceHash_NotPromptHash()
    {
        var baseline = Edition(refs: [Ref(1), Ref(2)]);
        foreach (var other in new[]
        {
            Edition(refs: [Ref(1), Ref(3)]),                                  // 내용
            Edition(refs: [Ref(1), Ref(2, ReferenceLabel.Ng, "scratch")]),    // 라벨 · 유형
            Edition(refs: [Ref(2), Ref(1)]),                                  // 모델이 본 순서
            Edition(refs: [Ref(1)]),                                          // 장수
        })
        {
            Assert.NotEqual(PartOf(baseline, "r"), PartOf(other, "r"));
            Assert.Equal(PartOf(baseline, "p"), PartOf(other, "p"));
        }
    }

    [Theory]
    [InlineData("thinking")]
    [InlineData("tokens")]
    [InlineData("dim")]
    public void InferenceSettings_ChangePromptHash(string which)
    {
        var baseline = Edition();
        var changed = which switch
        {
            "thinking" => Edition(options: new VlmOptions { ThinkingEffort = "high" }),
            "tokens" => Edition(options: new VlmOptions { MaxOutputTokens = 8192 }),
            _ => Edition(maxDim: 1024),
        };
        Assert.NotEqual(PartOf(baseline, "p"), PartOf(changed, "p"));
    }

    [Fact]
    public void TimeoutAndExpectedPart_DoNotChangeEdition()
    {
        Assert.Equal(Edition(), Edition(options: new VlmOptions { TimeoutSeconds = 30 }));
        Assert.Equal(Edition(Scenario(expectedPart: "A-100")), Edition(Scenario(expectedPart: "B-200")));
    }

    [Fact]
    public void VerificationPrompt_DiffersFromInspection()
    {
        Assert.NotEqual(Edition(), Edition(Scenario(expectedPart: "A-100")));
    }

    private static string PartOf(string edition, string key) =>
        edition.Split('|').Single(s => s.StartsWith(key + ":", StringComparison.Ordinal));
}
