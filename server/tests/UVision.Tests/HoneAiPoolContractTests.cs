using HoneAI;
using HoneAI.Trust;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 재확인 풀(<c>HoneAI.Core</c>)이 엔진이 기대는 대로 움직이는지 — 풀 항목 한 건의 추가 · 임대 · 응답 · 읽기 왕복. 엔진의 측정용 문제 흐름은
/// 이 동작 위에 서므로, 라이브러리 판이 올라 동작이 바뀌면 여기서 먼저 알아챈다.
/// </summary>
public sealed class HoneAiPoolContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uvision-pool-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static PoolItem Item(string decisionId, PoolItemKind kind = PoolItemKind.Plant) => new()
    {
        DecisionId = decisionId, Kind = kind, TierId = "vlm", SliceKey = "all", DecidedAt = Now, Probability = 1.0,
        Knob = "", KnobRates = new Dictionary<string, double>(), SnapshotVersion = "v1",
        InputRef = "2026-10-30/img1", ExpiresAt = Now.AddHours(1),
    };

    private static async Task<List<PoolEntry>> ReadAll(IRecheckPoolStore store)
    {
        var all = new List<PoolEntry>();
        await foreach (var e in store.ReadAsync(Now.AddDays(-1), Now.AddDays(1), CancellationToken.None))
            all.Add(e);
        return all;
    }

    [Fact]
    public async Task AddedItem_IsReadBack_WithoutLeaseOrResponse()
    {
        IRecheckPoolStore store = new JsonlRecheckPoolStore(_dir);

        Assert.True(await store.AddAsync(Item("d1"), CancellationToken.None)); // 새 항목 = true

        var entry = Assert.Single(await ReadAll(store));
        Assert.Equal("d1", entry.Item.DecisionId);
        Assert.Equal("2026-10-30/img1", entry.Item.InputRef);
        Assert.Null(entry.Lease);
        Assert.Null(entry.Response);
    }

    private static PoolResponse Answer(string decisionId, PoolPurpose purpose, RecheckOutcome outcome) => new()
    {
        Purpose = purpose, DecisionId = decisionId, TierId = "vlm", CheckedBy = "human", ReviewerKey = "dev-1",
        RespondedAt = Now.AddMinutes(1), Kind = PoolResponseKind.Answered, Outcome = outcome, UpperMustCatch = false,
    };

    [Fact]
    public async Task ARecheckItemsAnswer_LandsOnResponse_WhileAPlantAnswerIsFoundByItsPurpose()
    {
        IRecheckPoolStore store = new JsonlRecheckPoolStore(_dir);
        await store.AddAsync(Item("recheck", PoolItemKind.Recheck), CancellationToken.None);
        await store.AddAsync(Item("plant"), CancellationToken.None);
        await store.LeaseAsync(new PoolLease("recheck", "vlm", "dev-1", Now.AddMinutes(10)), CancellationToken.None);
        await store.LeaseAsync(new PoolLease("plant", "vlm", "dev-1", Now.AddMinutes(10)), CancellationToken.None);

        await store.RespondAsync(Answer("recheck", PoolPurpose.Recheck, RecheckOutcome.Disagreed), CancellationToken.None);
        await store.RespondAsync(Answer("plant", PoolPurpose.Plant, RecheckOutcome.Agreed), CancellationToken.None);

        var entries = (await ReadAll(store)).ToDictionary(e => e.Item.DecisionId);
        // 재확인 응답은 항목의 «대표 응답»(Response) 이다.
        Assert.Equal(RecheckOutcome.Disagreed, entries["recheck"].Response!.Outcome);
        Assert.Equal("dev-1", entries["recheck"].Lease!.ReviewerKey);
        // 정답 심기 응답은 대표 응답이 아니라 목적(Plant)으로 찾는다 — Response 로 읽으면 놓친다.
        Assert.Null(entries["plant"].Response);
        Assert.Equal(RecheckOutcome.Agreed, Assert.Single(entries["plant"].ResponsesOf(PoolPurpose.Plant)).Outcome);
    }

    [Fact]
    public async Task ALeaseIsVisibleToTheReader_BeforeAnyAnswer()
    {
        IRecheckPoolStore store = new JsonlRecheckPoolStore(_dir);
        await store.AddAsync(Item("leased"), CancellationToken.None);

        await store.LeaseAsync(new PoolLease("leased", "vlm", "dev-9", Now.AddMinutes(10)), CancellationToken.None);

        var entry = Assert.Single(await ReadAll(store));
        Assert.Equal("dev-9", entry.Lease!.ReviewerKey);
        Assert.Empty(entry.ResponsesOf(PoolPurpose.Plant));
        Assert.Null(entry.Response);
    }

    [Fact]
    public async Task AddingTheSameDecisionTwice_IsReportedNotDuplicated()
    {
        IRecheckPoolStore store = new JsonlRecheckPoolStore(_dir);

        Assert.True(await store.AddAsync(Item("dup"), CancellationToken.None));
        Assert.False(await store.AddAsync(Item("dup"), CancellationToken.None)); // 같은 결정 id = 이미 있음

        Assert.Single(await ReadAll(store));
    }

    [Fact]
    public async Task AStoreReopenedOnTheSameDirectory_SeesWhatWasWritten()
    {
        await new JsonlRecheckPoolStore(_dir).AddAsync(Item("persist"), CancellationToken.None);

        var entry = Assert.Single(await ReadAll(new JsonlRecheckPoolStore(_dir)));

        Assert.Equal("persist", entry.Item.DecisionId);
    }
}
