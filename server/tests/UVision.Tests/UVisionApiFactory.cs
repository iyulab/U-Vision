using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Tests;

/// <summary>
/// 통합 테스트용 팩토리 — <c>Storage:DataPath</c> 를 격리된 temp 디렉토리로 덮고
/// <c>demo</c> 시나리오를 seed 한다. 저장소가 파일시스템이므로 테스트마다 깨끗한 루트가 필요하다.
/// </summary>
public class UVisionApiFactory : WebApplicationFactory<Program>
{
    public string DataPath { get; } =
        Path.Combine(Path.GetTempPath(), "uvision-tests-" + Guid.NewGuid().ToString("N"));

    private StoragePaths Paths =>
        new(new StorageOptions { DataPath = DataPath }, AppContext.BaseDirectory);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataPath);
        builder.UseSetting($"{StorageOptions.SectionName}:DataPath", DataPath);

        SeedScenario(new Scenario
        {
            ScenarioId = "demo",
            Name = "데모 검사",
            Criteria = "제품 표면에 외관 결함이 없어야 한다. 결함이 보이면 NG, 깨끗하면 OK.",
        });
    }

    /// <summary>시나리오 정의를 디스크에 직접 기록한다(테스트 준비용).</summary>
    public void SeedScenario(Scenario scenario)
    {
        var path = Paths.ScenarioJson(scenario.ScenarioId);
        StoragePaths.AtomicWriteJsonAsync(path, scenario).GetAwaiter().GetResult();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataPath))
            Directory.Delete(DataPath, recursive: true);
    }
}
