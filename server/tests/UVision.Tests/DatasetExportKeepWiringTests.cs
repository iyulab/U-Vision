using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Configuration;
using Xunit;

namespace UVision.Tests;

/// <summary>export 생성 요청이 상한 정리를 부르는지 — 정리 규칙 자체는 <see cref="DatasetExportTrimmerTests"/> 가 본다.</summary>
public class DatasetExportKeepWiringTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;
    public DatasetExportKeepWiringTests(PinFactory factory) => _factory = factory;

    [Fact]
    public async Task Export_WithKeepOne_LeavesTheJustCreatedExport_AndTrimsTheOlderOnes()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton(new StorageOptions { DataPath = _factory.DataPath, DatasetExportKeep = 1 })));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);

        string? last = null;
        for (var i = 0; i < 3; i++)
        {
            var resp = await client.PostAsync("/api/u-vision/datasets/export?scenario_id=demo", null);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            last = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("export_id").GetString();
        }

        var list = await (await client.GetAsync("/api/u-vision/datasets?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        var ids = list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetProperty("export_id").GetString()).ToList();
        Assert.Contains(last, ids);
        Assert.True(ids.Count <= 2, $"상한 1 + 방금 만든 것 — 실제 {ids.Count}");
    }
}
