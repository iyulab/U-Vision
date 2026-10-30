using System;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>데이터 볼륨 여유 신호(<c>GET /health</c> 의 <c>data_volume</c>) — 디스크가 차기 전에 설치자가 알아채게 하는 읽기 전용 신호.</summary>
public class DataVolumeTests
{
    private static StoragePaths TempPaths(StorageOptions? options = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "uvision-vol-" + Guid.NewGuid().ToString("N"));
        var o = options ?? new StorageOptions();
        o.DataPath = root;
        return new StoragePaths(o, AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData(5L, 100L, 10L, true)]    // 여유 < 문턱
    [InlineData(10L, 100L, 10L, false)]  // 정확히 문턱 — 미만이 아니다
    [InlineData(50L, 100L, 10L, false)]
    [InlineData(0L, 100L, 0L, false)]    // 문턱 0 이하는 경고 없음(수치만)
    public void Evaluate_LowOnlyBelowThreshold(long free, long total, long warn, bool low)
    {
        var s = DataVolumeReader.Evaluate(free, total, warn);
        Assert.Equal(low, s.Low);
        Assert.Equal(free, s.FreeBytes);
        Assert.Equal(total, s.TotalBytes);
    }

    [Fact]
    public void Read_IsNull_WhenTheVolumeCannotBeMeasured()
    {
        // UNC 경로 · 준비되지 않은 드라이브 같은 환경 — 상태 확인이 저장소 사정으로 실패하지 않는다.
        var reader = new DataVolumeReader(TempPaths(), new StorageOptions(), _ => throw new IOException("not ready"));
        Assert.Null(reader.Read());
    }

    [Fact]
    public void Read_UsesTheConfiguredThreshold()
    {
        var reader = new DataVolumeReader(TempPaths(), new StorageOptions { LowSpaceWarningBytes = 1000 }, _ => (999, 5000));
        Assert.True(reader.Read()!.Low);
        reader = new DataVolumeReader(TempPaths(), new StorageOptions { LowSpaceWarningBytes = 1000 }, _ => (1000, 5000));
        Assert.False(reader.Read()!.Low);
    }

    [Fact]
    public void Read_RealVolume_GivesPlausibleNumbers()
    {
        var s = new DataVolumeReader(TempPaths(), new StorageOptions()).Read();
        Assert.NotNull(s);
        Assert.InRange(s!.FreeBytes, 0, s.TotalBytes);
        Assert.True(s.TotalBytes > 0);
    }

    [Fact]
    public async Task Health_CarriesDataVolume_AndKeepsStatusAndProvider()
    {
        using var factory = new UVisionApiFactory();
        var json = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/u-vision/health");

        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("mock", json.GetProperty("provider").GetString());
        var volume = json.GetProperty("data_volume");
        Assert.True(volume.GetProperty("total_bytes").GetInt64() > 0);
        Assert.True(volume.TryGetProperty("low", out _));
    }
}
