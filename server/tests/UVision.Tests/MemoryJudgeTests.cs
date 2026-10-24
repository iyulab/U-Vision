using UVision.Api.Configuration;
using UVision.Api.Services.Memory;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>확정 사례 기억 단의 순수 부분 — k-NN 판정 · 판 키 · 벡터 캐시 · 설정 검사.</summary>
public class MemoryJudgeTests
{
    private static float[] V(params float[] v) => VectorMath.Normalize(v);

    private static List<MemoryCase> Cases(string cls, int n, float[] around, float spread = 0.01f) =>
        [.. Enumerable.Range(0, n).Select(i => new MemoryCase($"img_{cls}_{i}", cls, V(around[0] + i * spread, around[1], around[2])))];

    [Fact]
    public void Answers_WhenAllNeighborsAgree_AndNearestWithinThreshold()
    {
        var memory = Cases("OK", 5, [1, 0, 0]).Concat(Cases("NG", 5, [0, 1, 0])).ToList();

        var answer = MemoryJudge.Decide(V(1, 0.01f, 0), memory, neighbors: 5, maxDistance: 0.15);

        Assert.Equal("OK", answer.Class);
        Assert.Null(answer.Abstained);
        Assert.Equal(5, answer.Neighbors);
        Assert.True(answer.NearestDistance < 0.01);
        Assert.Equal(1 - answer.NearestDistance!.Value, answer.Confidence, precision: 9);
    }

    [Fact]
    public void Abstains_WhenMemoryHasFewerCasesThanNeighbors()
    {
        var answer = MemoryJudge.Decide(V(1, 0, 0), Cases("OK", 4, [1, 0, 0]), neighbors: 5, maxDistance: 0.15);

        Assert.Null(answer.Class);
        Assert.Contains("4 < 5", answer.Abstained);
        Assert.Null(answer.NearestDistance);
    }

    [Fact]
    public void Abstains_WhenNeighborsDisagree()
    {
        // 질의가 두 무리 사이 — 최근접 5 개에 두 클래스가 섞인다.
        var memory = Cases("OK", 3, [1, 0, 0]).Concat(Cases("NG", 3, [0.9f, 0.1f, 0])).ToList();

        var answer = MemoryJudge.Decide(V(1, 0.05f, 0), memory, neighbors: 5, maxDistance: 0.15);

        Assert.Null(answer.Class);
        Assert.Equal("neighbors disagree", answer.Abstained);
        Assert.NotNull(answer.NearestDistance);
    }

    [Fact]
    public void Abstains_WhenNearestCaseIsBeyondThreshold()
    {
        var answer = MemoryJudge.Decide(V(0, 0, 1), Cases("OK", 5, [1, 0, 0]), neighbors: 5, maxDistance: 0.15);

        Assert.Null(answer.Class);
        Assert.Equal("nearest case beyond threshold", answer.Abstained);
        Assert.True(answer.NearestDistance > 0.15);
    }

    [Fact]
    public void ClassAgreement_IgnoresCase()
    {
        var memory = Cases("ok", 2, [1, 0, 0]).Concat(Cases("OK", 3, [1, 0.001f, 0])).ToList();

        Assert.NotNull(MemoryJudge.Decide(V(1, 0, 0), memory, neighbors: 5, maxDistance: 0.15).Class);
    }

    [Fact]
    public void Annotations_CarryNearestDistance_AndNeighborCount()
    {
        var a = new MemoryAnswer(null, 0, "neighbors disagree", 0.123456789, 5).ToAnnotations();

        Assert.Equal("5", a["memory:neighbors"]);
        Assert.Equal("0.123457", a["memory:nearest_distance"]);
    }

    [Theory]
    [InlineData("0123456789ABCDEF", "dinov2-small-0123456789ab")]
    [InlineData(null, "dinov2-small-1")]
    [InlineData("short", "dinov2-small-1")]
    public void EncoderKey_IsIdPlusShaPrefix_OrVersion(string? sha, string expected) =>
        Assert.Equal(expected, new EncoderIdentity("dinov2-small", "1", sha, 384, true).Key);

    [Fact]
    public async Task MockEmbedder_IsDeterministic_AndNormalized()
    {
        var embedder = new MockImageEmbedder();
        var a = await embedder.EmbedAsync([new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }, new byte[] { 9 }]);

        Assert.Equal(MockImageEmbedder.Identity, a.Encoder);
        Assert.Equal(0, VectorMath.CosineDistance(a.Vectors[0], a.Vectors[1]), precision: 6);
        Assert.True(VectorMath.CosineDistance(a.Vectors[0], a.Vectors[2]) > 0.01);
        Assert.Equal(1, Math.Sqrt(a.Vectors[0].Sum(x => (double)x * x)), precision: 5);
    }

    [Theory]
    [InlineData("none", typeof(DisabledImageEmbedder))]
    [InlineData("mock", typeof(MockImageEmbedder))]
    [InlineData("MLOOP", typeof(MloopImageEmbedder))]
    public void Factory_PicksProvider(string provider, Type expected) =>
        Assert.IsType(expected, ImageEmbedderFactory.Create(new MemoryOptions
        {
            Provider = provider, Endpoint = "http://mloop.test", Encoder = "dinov2-small",
        }));

    [Theory]
    [InlineData("faiss", "", "x", 5, 0.15)]       // 지어지지 않은 provider
    [InlineData("mloop", "", "x", 5, 0.15)]       // Endpoint 없음
    [InlineData("mloop", "http://m", "", 5, 0.15)] // Encoder 없음
    [InlineData("mock", "", "", 0, 0.15)]          // 이웃 0
    [InlineData("mock", "", "", 5, 0)]             // 문턱 0
    [InlineData("mock", "", "", 5, 2.5)]           // 코사인 거리 범위 밖
    public void Factory_RejectsBadConfig(string provider, string endpoint, string encoder, int neighbors, double maxDistance) =>
        Assert.Throws<ArgumentException>(() => ImageEmbedderFactory.Create(new MemoryOptions
        {
            Provider = provider, Endpoint = endpoint, Encoder = encoder, Neighbors = neighbors, MaxDistance = maxDistance,
        }));

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void Factory_RejectsTimeoutOutOfRange(int seconds) =>
        Assert.Throws<ArgumentException>(() => ImageEmbedderFactory.Create(new MemoryOptions
        {
            Provider = "mloop", Endpoint = "http://mloop.test", Encoder = "dinov2-small", TimeoutSeconds = seconds,
        }));

    [Fact]
    public async Task VectorStore_LastLineWins_SkipsTruncatedLine_AndRejectsBadKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uv-mem-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(new StorageOptions { DataPath = dir }, AppContext.BaseDirectory);
            var store = new FileMemoryVectorStore(paths);

            await store.AppendAsync("s1", "mock-1", [new("img_a", [1, 0]), new("img_b", [0, 1])]);
            await store.AppendAsync("s1", "mock-1", [new("img_a", [0.5f, 0.5f])]);
            await File.AppendAllTextAsync(paths.MemoryVectorsJsonl("s1", "mock-1"), "{\"image_id\":\"img_c\",\"vec");

            var read = await store.ReadAsync("s1", "mock-1");
            Assert.Equal(2, read.Count);
            Assert.Equal([0.5f, 0.5f], read["img_a"]);
            Assert.Empty(await store.ReadAsync("s1", "other-1"));
            Assert.Equal(Path.Combine(dir, "s1", "memory", "mock-1", "vectors.jsonl"), paths.MemoryVectorsJsonl("s1", "mock-1"));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("s1", "../x"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
