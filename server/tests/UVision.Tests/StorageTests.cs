using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 저장소 인프라 단위 테스트 — sanitize 거부, atomic overwrite, 저장·조회 왕복.
/// 통합 테스트(InspectTests)가 다루지 않는 파일시스템 메커니즘을 직접 검증한다.
/// </summary>
public class StorageTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "uvision-storage-" + Guid.NewGuid().ToString("N"));

    private StoragePaths Paths =>
        new(new StorageOptions { DataPath = _dataPath }, AppContext.BaseDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a:b")]
    public void Id_RejectsPathInjection(string bad)
    {
        // 변형이 아니라 거부 — 경로 주입·id 충돌 차단.
        Assert.Throws<ArgumentException>(() => StoragePaths.Id(bad));
    }

    [Theory]
    [InlineData("pcb-top")]
    [InlineData("demo_1")]
    [InlineData("img_ab12cd34")]
    public void Id_AcceptsSafeIdentifiers(string good)
    {
        Assert.Equal(good, StoragePaths.Id(good));
    }

    [Theory]
    [InlineData("2026-13-99")] // 형식만 검증(정규식) — 의미상 유효성은 도출 경로에서 보장
    [InlineData("2026/06/07")]
    [InlineData("not-a-date")]
    public void Date_RejectsMalformed(string bad) =>
        Assert.Throws<ArgumentException>(() => StoragePaths.Date(bad));

    [Fact]
    public async Task AtomicWrite_OverwritesExistingFile()
    {
        var path = Path.Combine(_dataPath, "x", "file.bin");
        await StoragePaths.AtomicWriteAsync(path, new byte[] { 1, 2, 3 });
        await StoragePaths.AtomicWriteAsync(path, new byte[] { 9, 9 }); // 기존 위로 overwrite

        Assert.Equal(new byte[] { 9, 9 }, await File.ReadAllBytesAsync(path));
        // temp 잔여물이 남지 않아야 한다.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
    }

    [Fact]
    public async Task InspectionStore_SaveThenList_RoundTrips()
    {
        var store = new FileInspectionStore(Paths);
        var result = new StoredResult
        {
            ScenarioId = "demo",
            ImageId = "img_test0001",
            Verdict = Verdict.NG,
            Findings = "표면 긁힘",
            Confidence = 0.87,
            Timestamp = "2026-06-07T10:30:00.0000000Z",
            ImageFile = "img_test0001.jpg",
        };

        await store.SaveAsync(new byte[] { 0xFF, 0xD8 }, ".jpg", result);
        var listed = await store.ListAsync("demo", "2026-06-07");

        var stored = Assert.Single(listed);
        Assert.Equal(result, stored); // record value equality — 전 필드 왕복
    }

    [Fact]
    public async Task InspectionStore_List_ReturnsEmpty_ForUnknownDate()
    {
        var store = new FileInspectionStore(Paths);
        var listed = await store.ListAsync("demo", "2099-01-01");
        Assert.Empty(listed);
    }

    [Fact]
    public async Task ScenarioStore_GetUnknown_ReturnsNull()
    {
        var store = new FileScenarioStore(Paths);
        Assert.Null(await store.GetAsync("ghost"));
    }

    [Fact]
    public async Task ScenarioStore_GetMalformed_Throws()
    {
        var store = new FileScenarioStore(Paths);
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("../etc"));
    }

    [Fact]
    public async Task ScenarioStore_RoundTrips_FullSchema()
    {
        var scenario = new Scenario
        {
            ScenarioId = "pcb-top",
            Name = "PCB 상면",
            Criteria = "솔더 브릿지 없음",
            Roi = new Roi { X = 0.1, Y = 0.2, W = 0.6, H = 0.5 },
            MotionThreshold = 15,
            StillFrames = 6,
            MinSharpness = 120,
            NgLabels = new() { ["ref1"] = "솔더 브릿지" },
        };
        await StoragePaths.AtomicWriteJsonAsync(Paths.ScenarioJson("pcb-top"), scenario);

        var loaded = await new FileScenarioStore(Paths).GetAsync("pcb-top");
        Assert.NotNull(loaded);
        // record 의 기본 Equals 는 Dictionary 를 참조 비교하므로, NgLabels 를 같은 인스턴스로
        // 치환해 나머지 필드(roi·캡처설정 포함)를 value 비교하고, ng_labels 는 따로 검증한다.
        var pivot = new Dictionary<string, string>();
        Assert.Equal(scenario with { NgLabels = pivot }, loaded with { NgLabels = pivot });
        Assert.Equal(scenario.NgLabels, loaded.NgLabels);
    }
}
