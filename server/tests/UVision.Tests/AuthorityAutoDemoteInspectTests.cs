using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// A1 인밴드 자동격하 핸들러 <b>통합</b> 테스트 — AutoDemoteEvaluator 단위·ShouldCheckAutoDemote 게이트
/// 단위에 더해, inspect 가 실제로 재집계→격하→authority 쓰기까지 배선됨을 end-to-end 로 검증한다.
/// (이 경로는 그간 단위만 있고 통합 미검증 — A1 후속 Minor.)
/// </summary>
public sealed class AuthorityAutoDemoteInspectTests : IClassFixture<AuthorityAutoDemoteInspectTests.Factory>
{
    private readonly Factory _factory;
    public AuthorityAutoDemoteInspectTests(Factory factory) => _factory = factory;

    /// <summary>ML=mock + 자동격하 즉시 발동(MinWindow=2·CheckEvery=1) 레버로 결정론 검증.</summary>
    public sealed class Factory : UVisionApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{UVisionOptions.SectionName}:Ml:Provider", "mock");
            builder.UseSetting($"{UVisionOptions.SectionName}:Authority:MinWindow", "2");
            builder.UseSetting($"{UVisionOptions.SectionName}:Authority:AutoDemoteCheckEvery", "1");
        }
    }

    [Fact]
    public async Task CoPrimary_MlUnderperforms_InspectAutoDemotesToAdvisory()
    {
        var scenarioId = $"demote_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = scenarioId, Name = "d", Criteria = "c" });

        var authority = (IAuthorityStore)_factory.Services.GetRequiredService<IAuthorityStore>();
        await authority.SetStageAsync(scenarioId, AuthorityStage.CoPrimary, "test", "promote", null, default);

        // ML 열화 근거를 시드: 정답 NG 2건에서 VLM=NG 적중·ML=OK 놓침 → ml_ng_recall 0 < vlm 1.
        var metrics = _factory.Services.GetRequiredService<IMetricsStore>();
        var labels = _factory.Services.GetRequiredService<ILabelStore>();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var id in new[] { "n1", "n2" })
        {
            await metrics.AppendAsync(scenarioId, new MetricsRow
            {
                ImageId = id, Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Verdict = Verdict.NG, VlmConfidence = 0.9, MlLabel = "OK", MlConfidence = 0.9,
                Agreement = false, RequiresReview = true, MlDegraded = false,
            });
            await labels.AppendLabelAsync(scenarioId, today, id, "NG", "test");
        }

        // inspect 1회 → 메트릭 row 추가(rows=3, CheckEvery=1 배수) → 인밴드 자동격하 발동.
        var resp = await _factory.CreateClient().PostAsync("/api/u-vision/inspect", InspectForm(scenarioId));
        resp.EnsureSuccessStatusCode();

        var state = await authority.ReadAsync(scenarioId, default);
        Assert.Equal(AuthorityStage.Advisory, state!.Stage); // co-primary → 안전쪽 1단계 격하
        Assert.Contains(state.History, t => t.Mode == "demote-auto");
    }

    [Fact]
    public async Task ABadNewModelIsNotHiddenByTheGoodOldModelsRowsOfTheSameDay()
    {
        var scenarioId = $"demmix_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = scenarioId, Name = "d", Criteria = "c" });

        var authority = _factory.Services.GetRequiredService<IAuthorityStore>();
        await authority.SetStageAsync(scenarioId, AuthorityStage.CoPrimary, "test", "promote", null, default);

        var metrics = _factory.Services.GetRequiredService<IMetricsStore>();
        var labels = _factory.Services.GetRequiredService<ILabelStore>();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        async Task Seed(string id, string model, Verdict vlm, string mlLabel)
        {
            await metrics.AppendAsync(scenarioId, new MetricsRow
            {
                ImageId = id, Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Verdict = vlm, VlmConfidence = 0.9, MlLabel = mlLabel, MlConfidence = 0.9, MlModel = model,
                Agreement = vlm.ToString() == mlLabel, RequiresReview = false, MlDegraded = false,
            });
            await labels.AppendLabelAsync(scenarioId, today, id, "NG", "test");
        }

        // 옛 좋은 판: ML 이 불량 60건을 모두 잡았고 VLM 은 놓쳤다. 같은 날 새 판: ML 이 2건을 놓쳤고 VLM 은 잡았다.
        // 창 전체로 재면 ML 재현율 60/62 = 0.968 ≥ floor 이고 VLM 2/62 보다 높아 격하하지 않는다(결함) — 새 판만으로 재면 0 < 1 이라 격하.
        for (int i = 0; i < 60; i++) await Seed($"old{i}", "sha-old", Verdict.OK, "NG");
        foreach (var id in new[] { "new1", "new2" }) await Seed(id, "sha-new", Verdict.NG, "OK");

        var resp = await _factory.CreateClient().PostAsync("/api/u-vision/inspect", InspectForm(scenarioId));
        resp.EnsureSuccessStatusCode();

        var state = await authority.ReadAsync(scenarioId, default);
        Assert.Equal(AuthorityStage.Advisory, state!.Stage);
        Assert.Contains(state.History, t => t.Mode == "demote-auto");
    }

    private static MultipartFormDataContent InspectForm(string scenarioId)
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
        var content = new MultipartFormDataContent();
        var img = new ByteArrayContent(bytes);
        img.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(img, "image", "capture.jpg");
        content.Add(new StringContent(scenarioId), "scenario_id");
        return content;
    }
}
