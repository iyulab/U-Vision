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

    // grammar 강제 없이 프롬프트로만 JSON 을 지시하므로(특히 reasoning 모델) 응답에 코드펜스·머리말이
    // 섞일 수 있다. ExtractJson 이 첫 '{' ~ 마지막 '}' 를 관용적으로 뽑는지 검증한다.
    [Theory]
    [InlineData("{\"verdict\":\"OK\"}", "{\"verdict\":\"OK\"}")]
    [InlineData("```json\n{\"verdict\":\"NG\"}\n```", "{\"verdict\":\"NG\"}")]
    [InlineData("판정 결과입니다: {\"verdict\":\"OK\"} 이상.", "{\"verdict\":\"OK\"}")]
    [InlineData("no json here", "no json here")]
    public void ExtractJson_StripsFencesAndPreamble(string input, string expected)
    {
        Assert.Equal(expected, IronHiveVlmProvider.ExtractJson(input));
    }
}
