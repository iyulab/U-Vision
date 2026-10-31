using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Questions;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>문제 저장소의 메모리 색인 — 목록이 파일을 다시 읽지 않아도 쓰기를 따라가고, 시나리오가 지워지면 비워진다.</summary>
public class QuestionStoreIndexTests : IDisposable
{
    private const string Scenario = "idx";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvtest_" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public QuestionStoreIndexTests() =>
        _paths = new StoragePaths(new StorageOptions { DataPath = _root }, _root);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static StoredQuestion Q(string id, string image) => new()
    {
        QuestionId = id, ScenarioId = Scenario, Date = "2026-10-31", ImageId = image, Form = QuestionForm.Single,
        Stream = LabelStream.Learn, Reason = QuestionReason.Random, CreatedAt = "2026-10-31T10:00:00.0000000Z",
        AskedTo = "dev", State = QuestionState.Open,
    };

    [Fact]
    public async Task List_FollowsCreateAndClose_WithoutRereadingTheFiles()
    {
        var store = new FileQuestionStore(_paths);
        await store.CreateAsync(Q("q1", "a"));
        Assert.Single(await store.ListAsync(Scenario)); // 처음 적재

        await store.CreateAsync(Q("q2", "b"));
        await store.TryCloseAsync(Scenario, "q1", QuestionState.Answered, "OK", "dev");
        // 파일을 지워도 색인이 답한다 — 목록은 이 저장소의 쓰기를 따라가는 파생물이다.
        File.Delete(_paths.QuestionJson(Scenario, "q2"));

        var listed = (await store.ListAsync(Scenario)).OrderBy(q => q.QuestionId).ToList();
        Assert.Equal(["q1", "q2"], listed.Select(q => q.QuestionId));
        Assert.Equal(QuestionState.Answered, listed[0].State);
        Assert.Equal("OK", listed[0].Answer);
    }

    [Fact]
    public async Task List_LoadsExistingFilesOnFirstCall_AndDropsTheIndexWhenTheScenarioFolderIsGone()
    {
        await new FileQuestionStore(_paths).CreateAsync(Q("q1", "a")); // 이전 프로세스가 쓴 파일
        var store = new FileQuestionStore(_paths);                     // 새 프로세스
        Assert.Single(await store.ListAsync(Scenario));

        Directory.Delete(_paths.ScenarioDir(Scenario), true);          // 시나리오 삭제
        Assert.Empty(await store.ListAsync(Scenario));

        await store.CreateAsync(Q("q9", "z"));                         // 같은 id 로 다시 만들어도 낡은 문제가 되살아나지 않는다
        Assert.Equal(["q9"], (await store.ListAsync(Scenario)).Select(q => q.QuestionId));
    }
}
