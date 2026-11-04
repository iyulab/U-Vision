using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// Windows 는 누가 파일을 열고 있는 동안의 덮어쓰기 이동을 거절한다 — 에이전트가 노드 파일을 계속 교체하고 콘솔이 목록을 읽는 것처럼
/// 쓰기와 읽기가 겹치는 자리에서 쓰기도 읽기도 던지지 않아야 한다(전체 스위트 부하에서만 나던 플레이크의 근본 원인).
/// 다른 OS 에서는 이 경합이 없어 이 시험은 그냥 통과한다.
/// </summary>
public class AtomicWriteRaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uvision-race-" + Guid.NewGuid().ToString("N"));

    public AtomicWriteRaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ConcurrentReadsAndReplacements_NeverThrow_AndNeverSeeAHalfFile()
    {
        var path = Path.Combine(_dir, "node.json");
        await StoragePaths.AtomicWriteAsync(path, System.Text.Encoding.UTF8.GetBytes("{\"n\":0,\"pad\":\"" + new string('x', 2000) + "\"}"));
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                await using var stream = await StoragePaths.OpenReadResilientAsync(path);
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream); // 반쪽 파일이면 JsonException
                Assert.True(doc.RootElement.TryGetProperty("n", out _));
                reads++;
            }
            return reads;
        });

        for (var i = 1; i <= 300; i++)
            await StoragePaths.AtomicWriteAsync(path, System.Text.Encoding.UTF8.GetBytes("{\"n\":" + i + ",\"pad\":\"" + new string('y', 2000) + "\"}"));
        await stop.CancelAsync();

        Assert.True(await reader > 0);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*")); // 임시 파일이 남지 않는다
    }
}
