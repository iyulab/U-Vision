using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 레버가 <c>/api/inspect</c> 에서 <b>실제로 provider 입력에 적용되는지</b> 결정론적으로 검증한다.
///
/// ⚠️ 라이브 latency 측정(C30)은 GPUStack prefix-cache 에 교란돼 "다운스케일 적용"과 "무시"를 구분하지
/// 못한다(동일 바이트는 캐시 히트로 빨라짐). 여기서는 <see cref="CapturingVlmProvider"/> 가 provider 가
/// 실제로 받은 이미지의 치수·기준이미지 장수를 포착해 배선을 못박는다(캐시·키 무관, 회귀 가드).
/// </summary>
public class LeverWiringTests : IClassFixture<UVisionApiFactory>
{
    private readonly UVisionApiFactory _factory;

    public LeverWiringTests(UVisionApiFactory factory) => _factory = factory;

    private static byte[] MakeJpeg(int width, int height)
    {
        using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private static int LongestSide(ReadOnlyMemory<byte> data)
    {
        using var image = Image.Load(data.Span);
        return Math.Max(image.Width, image.Height);
    }

    private static MultipartFormDataContent Form(byte[] bytes, string scenarioId)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        return form;
    }

    /// <summary>spy 인스턴스를 provider 로 주입한 클라이언트.</summary>
    private HttpClient ClientWith(CapturingVlmProvider spy) =>
        _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IVlmProvider>(spy))).CreateClient();

    [Fact]
    public async Task Inspect_AppliesDownscale_ToImageReachingProvider()
    {
        // 서버가 max_image_dimension=256 으로 query 를 축소해 provider 에 넘기는지(배선 증명).
        _factory.SeedScenario(new Scenario { ScenarioId = "lever-256", Name = "x", MaxImageDimension = 256 });
        var spy = new CapturingVlmProvider();

        var resp = await ClientWith(spy).PostAsync("/api/u-vision/inspect", Form(MakeJpeg(800, 600), "lever-256"));
        resp.EnsureSuccessStatusCode();

        Assert.Equal(256, LongestSide(spy.LastImage)); // 800 → 256 으로 서버가 축소
    }

    [Fact]
    public async Task Inspect_PassesThroughImage_WhenMaxImageDimensionZero()
    {
        // max_image_dimension=0(기본) → 축소 없이 원본 그대로 provider 에 도달.
        _factory.SeedScenario(new Scenario { ScenarioId = "lever-0", Name = "x", MaxImageDimension = 0 });
        var spy = new CapturingVlmProvider();

        var resp = await ClientWith(spy).PostAsync("/api/u-vision/inspect", Form(MakeJpeg(800, 600), "lever-0"));
        resp.EnsureSuccessStatusCode();

        Assert.Equal(800, LongestSide(spy.LastImage)); // 원본 유지(다운스케일 비활성)
    }

    [Fact]
    public async Task Inspect_CapsReferences_PerLabel_ToReferenceCap()
    {
        // reference_cap=2 인데 라벨당 4장 시드 → provider 는 라벨당 2장(총 4)만 받아야 한다(cap 적용).
        _factory.SeedScenario(new Scenario { ScenarioId = "cap-2", Name = "x", ReferenceCap = 2 });
        for (var i = 0; i < 4; i++)
        {
            _factory.SeedReference("cap-2", ReferenceLabel.Ok, MakeJpeg(64, 64));
            _factory.SeedReference("cap-2", ReferenceLabel.Ng, MakeJpeg(64, 64));
        }
        var spy = new CapturingVlmProvider();

        var resp = await ClientWith(spy).PostAsync("/api/u-vision/inspect", Form(MakeJpeg(100, 100), "cap-2"));
        resp.EnsureSuccessStatusCode();

        Assert.Equal(4, spy.LastReferenceCount); // 2 OK + 2 NG (8장 중 cap)
    }

    [Fact]
    public async Task Inspect_ZeroReferenceCap_SendsNoReferences()
    {
        // reference_cap=0 → zero-shot(기준이미지 시드돼 있어도 provider 에 0장).
        _factory.SeedScenario(new Scenario { ScenarioId = "cap-0", Name = "x", ReferenceCap = 0 });
        _factory.SeedReference("cap-0", ReferenceLabel.Ok, MakeJpeg(64, 64));
        _factory.SeedReference("cap-0", ReferenceLabel.Ng, MakeJpeg(64, 64));
        var spy = new CapturingVlmProvider();

        var resp = await ClientWith(spy).PostAsync("/api/u-vision/inspect", Form(MakeJpeg(100, 100), "cap-0"));
        resp.EnsureSuccessStatusCode();

        Assert.Equal(0, spy.LastReferenceCount); // zero-shot
    }

    /// <summary>provider 가 실제로 받은 입력(query 치수·refs 장수)을 포착하는 spy.</summary>
    private sealed class CapturingVlmProvider : IVlmProvider
    {
        public string Name => "capture";
        public ReadOnlyMemory<byte> LastImage { get; private set; }
        public int LastReferenceCount { get; private set; }

        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default)
        {
            LastImage = image;
            LastReferenceCount = scenario.References.Count;
            return Task.FromResult(new InspectionResult { Verdict = Verdict.OK, Confidence = 0.9 });
        }
    }
}
