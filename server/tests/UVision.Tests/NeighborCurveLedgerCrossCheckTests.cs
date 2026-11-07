extern alias bench;

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
using Xunit;
using BenchLedgerNeighbors = bench::UVision.Benchmark.LedgerNeighbors;

namespace UVision.Tests;

/// <summary>
/// 엔진이 실제로 쌓은 장부 위에서 두 길이 같은 값을 내야 한다 — <c>GET /memory</c> 의 <c>neighbor_curve</c> 와 측정 도구
/// <c>ledger-neighbors</c> 가 같은 장부를 다시 센 표. 둘 다 <c>MemoryJudge.Reconsider</c> 위에 서 있지만 날짜 창 · 라벨 결합 ·
/// 줄 걸러내기는 각자의 결선이라, 단위 시험이 못 보는 어긋남(한쪽만 줄을 빼는 일 등)을 여기서 잡는다.
/// 장부는 mock 임베더로 24 장을 차례로 판정 · 라벨 · 동기화해 쌓는다 — 기억이 자라는 동안의 줄이라 이웃 기록 길이가 1 에서 9 로 늘어
/// k 마다 셀 수 있는 줄 수가 다르다.
/// </summary>
public class NeighborCurveLedgerCrossCheckTests
{
    private sealed class Factory : UVisionApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Provider", "mock");
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Neighbors", "5");
            // mock 벡터는 서로 멀다 — 문턱을 끝(2)까지 열어 «이웃이 모이는가» 만 k 를 가르게 한다.
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:MaxDistance", "2");
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
        form.Add(new StringContent("demo"), "scenario_id");
        return form;
    }

    private static byte[] Jpeg(byte tail) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, tail];

    [Fact]
    public async Task EndpointCurve_EqualsTheBenchmarkRecount_OfTheSameEngineLedger()
    {
        using var factory = new Factory();
        var client = factory.CreateClient();
        var memory = factory.Services.GetRequiredService<CaseMemory>();

        const int images = 24;
        for (byte i = 0; i < images; i++)
        {
            var id = (await (await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(i)))).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("image_id").GetString()!;
            // 불량 셋 중 하나 — 이웃 클래스가 섞여 k 가 클수록 이웃이 갈린다.
            await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
            { ScenarioId = "demo", Date = Today, ImageId = id, Label = i % 3 == 0 ? "NG" : "OK" });
            await memory.SyncOnceAsync(CancellationToken.None);
        }

        var report = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        var curve = report.GetProperty("recommendation").GetProperty("neighbor_curve");
        var endpoint = curve.GetProperty("rows").EnumerateArray()
            .Select(r => (K: r.GetProperty("k").GetInt32(), Counted: r.GetProperty("counted").GetInt32(),
                Answered: r.GetProperty("answered").GetInt32(), Missed: r.GetProperty("missed").GetInt32()))
            .ToList();

        var outPath = Path.Combine(factory.DataPath, "ledger-neighbors.md");
        Assert.Equal(0, await BenchLedgerNeighbors.RunAsync(new Dictionary<string, string>
        {
            ["data"] = factory.DataPath, ["scenario"] = "demo", ["k"] = "1,3,5,9", ["max-distance"] = "2", ["out"] = outPath,
        }));
        var bench = File.ReadAllLines(outPath)
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries))
            .Where(c => c.Length > 6 && int.TryParse(c[1], out _))
            .Select(c => (K: int.Parse(c[1], CultureInfo.InvariantCulture), Answered: int.Parse(c[2], CultureInfo.InvariantCulture),
                Missed: int.Parse(c[5], CultureInfo.InvariantCulture)))
            .ToList();

        Assert.Equal([1, 3, 5, 9], endpoint.Select(r => r.K));
        Assert.Equal(bench, endpoint.Select(r => (r.K, r.Answered, r.Missed)));
        // 시험이 의미를 가지려면 k 가 실제로 답을 가른 장부여야 하고, 기억이 자라는 동안의 줄이라 k 9 는 센 줄이 더 적다.
        Assert.True(endpoint.Select(r => r.Answered).Distinct().Count() > 1);
        Assert.True(endpoint.Single(r => r.K == 9).Counted < endpoint.Single(r => r.K == 1).Counted);
    }
}
