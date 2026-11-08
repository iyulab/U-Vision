using System.Text.Json;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 상태 파일을 다른 쪽이 교체하거나 덧붙이는 동안 읽어도 안 던지고, 읽는 쪽이 쓰는 쪽을 막지 않는다 — Windows 는 열려 있는 파일의 덮어쓰기 이동과
/// <c>FileShare.Read</c> 로 열린 파일의 덧붙이기 열기를 거절한다(핵심은 <see cref="StoragePaths.OpenReadResilientAsync"/> 의 공유 모드와 재시도).
/// 다른 OS 에는 이 경합이 없어 이 시험은 거기서는 «그냥 통과» 한다 — 의미는 Windows 에서 진다.
/// </summary>
public sealed class StoragePathsResilienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uvr-" + Guid.NewGuid().ToString("N")[..8]);

    public StoragePathsResilienceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Replace_WhileReading_NeverThrows_AndNeverReadsAHalfFile()
    {
        var path = Path.Combine(_dir, "state.json");
        await StoragePaths.AtomicWriteJsonAsync(path, new Payload(0, new string('x', 4000)));
        using var stop = new CancellationTokenSource(Window);

        var writer = Task.Run(async () =>
        {
            for (var i = 1; !stop.IsCancellationRequested; i++)
                await StoragePaths.AtomicWriteJsonAsync(path, new Payload(i, new string('x', 4000)));
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                var bytes = await StoragePaths.ReadAllBytesResilientAsync(path);
                Assert.NotNull(JsonSerializer.Deserialize<Payload>(bytes, StoragePaths.Json));   // 반쪽 파일이면 여기서 JsonException
                reads++;
            }
            return reads;
        })).ToArray();

        await writer;
        var total = (await Task.WhenAll(readers)).Sum();
        Assert.True(total > 50, $"읽기가 거의 안 돌았다({total}) — 시험이 경합을 만들지 못했다");
    }

    [Fact]
    public async Task ReadingLines_NeverBlocksAnAppender_AndSeesWhatWasAppended()
    {
        var path = Path.Combine(_dir, "metrics.jsonl");
        using var stop = new CancellationTokenSource(Window);
        var written = 0;

        // 쓰는 쪽은 저장소가 하는 대로 — 매번 FileShare.Read 로 열어 한 줄 덧붙인다.
        var appender = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                await stream.WriteAsync("{\"n\":1}\n"u8.ToArray());
                written++;
            }
        });
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(path)) _ = await StoragePaths.ReadAllLinesResilientAsync(path);
            }
        });

        await appender;   // 읽는 쪽이 막았다면 여기서 IOException(공유 위반)이 올라온다
        await reader;
        var lines = await StoragePaths.ReadAllLinesResilientAsync(path);
        Assert.Equal(written, lines.Count);
        Assert.True(written > 50, $"덧붙이기가 거의 안 돌았다({written})");
    }

    [Fact]
    public async Task ReadAllText_And_Lines_MatchTheSimpleReads_WhenNothingRaces()
    {
        var path = Path.Combine(_dir, "plain.txt");
        await File.WriteAllTextAsync(path, "첫째\n둘째\r\n셋째");   // 마지막 줄바꿈 없음 · CRLF 섞임 — File.ReadAllLines 와 같은 줄이 나와야 한다

        Assert.Equal(await File.ReadAllLinesAsync(path), await StoragePaths.ReadAllLinesResilientAsync(path));
        Assert.Equal(await File.ReadAllTextAsync(path), await StoragePaths.ReadAllTextResilientAsync(path));
        Assert.Equal(await File.ReadAllBytesAsync(path), await StoragePaths.ReadAllBytesResilientAsync(path));
        await Assert.ThrowsAsync<FileNotFoundException>(() => StoragePaths.ReadAllBytesResilientAsync(Path.Combine(_dir, "none")));
    }

    // 재시도 예산은 개수가 아니라 벽시계다 — 부하로 쓰는 쪽이 굶은 만큼 같이 늘어야 한다(전체 스위트 부하에서 40 번(약 0.4 초)이 소진된 적이 있다).
    // 다른 쪽이 파일을 0.4 초보다 오래(여기서는 0.9 초) 독점하는 상황을 직접 만든다. Windows 에서만 독점 열기가 이동 · 열기를 거절한다.
    [Fact]
    public async Task Replace_WaitsOutAnOverlapLongerThanTheOldFixedCount()
    {
        var path = Path.Combine(_dir, "held.json");
        await StoragePaths.AtomicWriteJsonAsync(path, new Payload(0, "a"));
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(TimeSpan.FromMilliseconds(900)); holder.Dispose(); });

        await StoragePaths.AtomicWriteJsonAsync(path, new Payload(1, "b"));   // 예산이 개수 40 번(약 0.4 초)이면 여기서 던진다

        await release;
        var back = JsonSerializer.Deserialize<Payload>(await StoragePaths.ReadAllBytesResilientAsync(path), StoragePaths.Json);
        Assert.Equal(1, back!.N);
    }

    [Fact]
    public async Task Read_WaitsOutAnExclusiveWriterLongerThanTheOldFixedCount()
    {
        var path = Path.Combine(_dir, "writing.json");
        await File.WriteAllTextAsync(path, "{\"n\":7}");
        var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(TimeSpan.FromMilliseconds(900)); writer.Dispose(); });

        var bytes = await StoragePaths.ReadAllBytesResilientAsync(path);   // 같은 이유로 읽는 쪽도 벽시계 예산이어야 한다

        await release;
        Assert.Equal("{\"n\":7}", System.Text.Encoding.UTF8.GetString(bytes));
    }

    private sealed record Payload(int N, string Pad);
}
