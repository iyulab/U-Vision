using IronHive.Abstractions.Messages.Content;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// few-shot 결선(IronHiveVlmProvider.BuildContent) 단위 검증 — 키 없이 wiring 을 확인한다.
///
/// ⚠️ 정직성: 이 테스트는 기준 이미지가 요청 본문에 올바른 개수·순서·형식으로 결합되는지만 검증한다.
/// few-shot 이 실제로 판정을 개선하는지(판정 효과)는 M0.1(실 provider + 대표 이미지)에서만 확인된다.
/// </summary>
public class FewShotWiringTests
{
    private static readonly byte[] Target = { 1, 1, 1 };

    [Fact]
    public void BuildContent_NoReferences_IsZeroShot()
    {
        var content = IronHiveVlmProvider.BuildContent(Target, []);

        // 텍스트 1 + 대상 이미지 1만.
        Assert.Single(content.OfType<ImageMessageContent>());
        Assert.Equal(ImageFormat.Jpeg, content.OfType<ImageMessageContent>().Single().Format);
    }

    [Fact]
    public void BuildContent_WithReferences_AttachesInOrder()
    {
        var references = new List<ReferenceImage>
        {
            new() { Data = new byte[] { 2 }, Label = ReferenceLabel.Ok },
            new() { Data = new byte[] { 3 }, Label = ReferenceLabel.Ok },
            new() { Data = new byte[] { 4 }, Label = ReferenceLabel.Ng, NgLabel = "솔더 브릿지", IsPng = true },
        };

        var content = IronHiveVlmProvider.BuildContent(Target, references);
        var images = content.OfType<ImageMessageContent>().ToList();

        // 기준 3장 + 대상 1장.
        Assert.Equal(4, images.Count);
        // 대상 이미지는 마지막이며 jpeg.
        Assert.Equal(Convert.ToBase64String(Target), images[^1].Base64);
        Assert.Equal(ImageFormat.Jpeg, images[^1].Format);
        // NG 기준은 png 형식이 보존된다.
        Assert.Equal(ImageFormat.Png, images[2].Format);
        // NG 레이블 텍스트가 본문에 포함된다.
        Assert.Contains(
            content.OfType<TextMessageContent>(), t => t.Value.Contains("솔더 브릿지"));
    }

    [Theory]
    [InlineData("scratch", "[NG 기준 — scratch(긁힘)]")]
    [InlineData("긁힘", "[NG 기준 — scratch(긁힘)]")]
    [InlineData("DENT", "[NG 기준 — dent]")]
    [InlineData("녹", "[NG 기준 — 녹]")]
    [InlineData(null, "[NG 기준]")]
    public void ReferenceTag_NamesADeclaredDefectTypeByIdAndName(string? ngLabel, string expected)
    {
        IReadOnlyList<ScenarioClass> defects = [new() { Id = "scratch", Label = "긁힘" }, new() { Id = "dent" }];
        var reference = new ReferenceImage { Data = new byte[] { 1 }, Label = ReferenceLabel.Ng, NgLabel = ngLabel };

        Assert.Equal(expected, VlmPrompt.ReferenceTag(reference, defects));
        Assert.Equal(ngLabel is null ? "[NG 기준]" : $"[NG 기준 — {ngLabel}]", VlmPrompt.ReferenceTag(reference, null));
    }

    [Fact]
    public void BuildContent_PngTarget_IsLabeledPng()
    {
        // 다운스케일 레버가 꺼져 있으면 업로드 원본이 그대로 온다 — PNG 바이트를 JPEG 로 표기하면 안 된다.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];

        var content = IronHiveVlmProvider.BuildContent(png, []);

        Assert.Equal(ImageFormat.Png, content.OfType<ImageMessageContent>().Single().Format);
    }

    // 구조화 출력(json_schema grammar)이 content 를 강제하므로 보통 순수 JSON 이 오지만, reasoning
    // 모델/백엔드가 코드펜스·머리말을 덧붙일 가능성에 대한 defense-in-depth. ExtractJson 이 첫 '{' ~
    // 마지막 '}' 를 관용적으로 뽑는지 검증한다.
    [Theory]
    [InlineData("{\"verdict\":\"OK\"}", "{\"verdict\":\"OK\"}")]
    [InlineData("```json\n{\"verdict\":\"NG\"}\n```", "{\"verdict\":\"NG\"}")]
    [InlineData("판정 결과입니다: {\"verdict\":\"OK\"} 이상.", "{\"verdict\":\"OK\"}")]
    [InlineData("no json here", "no json here")]
    public void ExtractJson_StripsFencesAndPreamble(string input, string expected)
    {
        Assert.Equal(expected, IronHiveVlmProvider.ExtractJson(input));
    }

    // 구조화 출력 스키마는 confidence 를 number 로만 강제하고 범위는 강제 못 한다 — 모델이 백분율(95.0)로
    // 방출할 수 있다. 경계에서 0.0~1.0 불변식을 강제하는지 검증한다.
    [Theory]
    [InlineData(0.95, 0.95)]   // 이미 정상
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(95.0, 0.95)]   // 백분율 → 소수
    [InlineData(100.0, 1.0)]
    [InlineData(90.0, 0.90)]
    [InlineData(150.0, 1.0)]   // 범위 밖 → clamp
    [InlineData(-0.5, 0.0)]    // 음수 → clamp
    public void NormalizeConfidence_EnforcesUnitRange(double input, double expected)
    {
        Assert.Equal(expected, IronHiveVlmProvider.NormalizeConfidence(input), 3);
    }
}
