using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HoneAI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 선별(<c>classification</c>)의 기억 단 — k-NN 은 클래스에 중립이라 OK 가 없는 클래스에도 답하고, 패치 이상 점수 은행은
/// <c>OK</c> 클래스 사례로만 서므로 OK 가 없으면 은행 없이 동기화가 정상 동작한다(계획 P2).
/// </summary>
public class ClassificationMemoryTests
{
    private const string Id = "parts";

    private sealed class MemoryFactory : UVisionApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Provider", "mock");
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Neighbors", "1");
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Patches", "true"); // 은행이 설 수 없는 구성에서도 예외 없음을 본다
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:SyncIntervalSeconds", "3600");
        }
    }

    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static MultipartFormDataContent Form(byte[] image)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(Id), "scenario_id");
        return form;
    }

    private static byte[] Jpeg(byte tail) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, tail];

    private static async Task SeedAsync(UVisionApiFactory factory)
    {
        factory.CreateClient();
        var dir = Path.Combine(factory.DataPath, Id);
        Directory.CreateDirectory(dir);
        var scenario = new Scenario
        {
            ScenarioId = Id, Name = "구분", Type = ScenarioVocabulary.Classification,
            Classes = [new() { Id = "A", Settleable = true }, new() { Id = "B" }],
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "scenario.json"), JsonSerializer.Serialize(scenario, StoragePaths.Json));
    }

    private sealed class FixedVlm(string picked) : IVlmProvider
    {
        public string Name => "fixed";

        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InspectionResult { Verdict = Verdict.OK, Findings = "", Confidence = 0.9, Class = picked });
    }

    [Theory]
    [InlineData("A", "shadowed")]    // 확정해도 되는 클래스 — 기억이 답하면 Shadow 로 남는다
    [InlineData("B", "escalated")]   // 확정 권한이 없는 클래스 — 위로 올린다
    public async Task ConfirmedCase_IsRecalledByClass_WithoutAnOkClassOrPatchBank(string confirmed, string memoryOutcome)
    {
        using var baseFactory = new MemoryFactory();
        await SeedAsync(baseFactory);
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IVlmProvider>(new FixedVlm(confirmed))));
        var client = factory.CreateClient();
        var image = Jpeg(7);

        var first = await (await client.PostAsync("/api/u-vision/v2/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>();
        var put = await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = Id, Date = Today, ImageId = first.GetProperty("decision_id").GetString()!, Label = confirmed });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var memory = factory.Services.GetRequiredService<CaseMemory>();
        Assert.Equal(1, await memory.SyncOnceAsync(CancellationToken.None)); // OK 가 없어도 지어진다(패치 은행만 못 선다)

        var v2 = await (await client.PostAsync("/api/u-vision/v2/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>();
        var step = v2.GetProperty("path").EnumerateArray().First(s => s.GetProperty("tier").GetString() == "memory");
        Assert.Equal(memoryOutcome, step.GetProperty("outcome").GetString());
        Assert.Equal(confirmed, v2.GetProperty("class").GetString());
        Assert.Equal("vlm", v2.GetProperty("settled_by").GetProperty("tier").GetString()); // 기억은 아직 Shadow
    }
}
