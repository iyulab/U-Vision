using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Questions;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 보존 삭제 — 되돌릴 수 없는 행위라 경계를 촘촘히 시험한다: PIN · 계획만이 기본 · 지문을 되받아야 삭제 · 오늘 제외 ·
/// 구간 밖 · 고르지 않은 부류 · 다른 시나리오를 건드리지 않음 · 삭제 사실 기록 · export 권고. 테스트마다 서로 다른 연도의 날짜를 써서
/// 한 팩토리(데이터 루트)를 나눠 써도 겹치지 않는다.
/// </summary>
public class RetentionEndpointTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;
    public RetentionEndpointTests(PinFactory factory) => _factory = factory;

    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private const string Url = "/api/u-vision/scenarios/demo/retention/purge";

    private HttpClient PinClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return client;
    }

    private string Dir(string scenario, string date) => Path.Combine(_factory.DataPath, scenario, date);

    /// <summary>날짜 폴더에 검사 한 건(사진 · 레코드 · 이상 지도)을 놓고, <paramref name="label"/> 이 있으면 사람 라벨 사이드카도 놓는다.</summary>
    private void SeedRecord(string scenario, string date, string id, string? label = null)
    {
        var dir = Dir(scenario, date);
        Directory.CreateDirectory(Path.Combine(dir, "anomaly-maps"));
        File.WriteAllBytes(Path.Combine(dir, id + ".jpg"), Jpeg);
        File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(new StoredResult
        {
            ScenarioId = scenario, ImageId = id, Verdict = Verdict.OK, Findings = "", Confidence = 0.9,
            Timestamp = date + "T09:00:00.0000000+00:00", ImageFile = id + ".jpg",
        }, StoragePaths.Json));
        File.WriteAllText(Path.Combine(dir, "anomaly-maps", id + ".json"), "{}");
        if (label is not null)
            File.WriteAllText(Path.Combine(dir, id + ".label.json"),
                JsonSerializer.Serialize(new StoredLabel { ImageId = id, Label = label, Timestamp = "t" }, StoragePaths.Json));
    }

    private void SeedJsonl(string scenario, string kind, string date, int rows)
    {
        var dir = Path.Combine(_factory.DataPath, scenario, kind);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, date + ".jsonl"), Enumerable.Range(0, rows).Select(i => $"{{\"n\":{i}}}"));
    }

    /// <summary>삭제 기록 줄 수 — 같은 팩토리를 쓰는 다른 테스트가 먼저 남겼을 수 있어 「늘지 않았다」로 본다.</summary>
    private int RetentionLines(string scenario)
    {
        var path = Path.Combine(_factory.DataPath, scenario, "retention.jsonl");
        return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
    }

    private bool Exists(string scenario, string kind, string date) =>
        File.Exists(Path.Combine(_factory.DataPath, scenario, kind, date + ".jsonl"));

    private static StringContent Body(object body) =>
        new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task WithoutPin_IsRejected()
    {
        var resp = await _factory.CreateClient().PostAsync(Url,
            Body(new { from = "2019-01-01", to = "2019-01-31", kinds = new[] { "records" } }));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task OmittedDryRun_IsAPlan_AndDeletesNothing()
    {
        var client = PinClient();
        var linesBefore = RetentionLines("demo");
        SeedRecord("demo", "2020-01-10", "a", "NG");
        SeedRecord("demo", "2020-01-10", "b"); // 라벨 없음
        SeedJsonl("demo", "ledger", "2020-01-10", 3);
        SeedJsonl("demo", "metrics", "2020-01-10", 2);

        var resp = await client.PostAsync(Url,
            Body(new { from = "2020-01-01", to = "2020-01-31", kinds = new[] { "records", "ledger", "metrics" } }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var plan = await Json(resp);
        Assert.True(plan.GetProperty("dry_run").GetBoolean());
        Assert.False(plan.GetProperty("purged").GetBoolean());
        Assert.Equal(64, plan.GetProperty("plan_fingerprint").GetString()!.Length);
        var kinds = plan.GetProperty("kinds").EnumerateArray().ToDictionary(k => k.GetProperty("kind").GetString()!);
        Assert.Equal(1, kinds["records"].GetProperty("days").GetInt32());
        Assert.Equal(7, kinds["records"].GetProperty("files").GetInt32()); // 사진 2 + 레코드 2 + 라벨 1 + 이상 지도 2
        Assert.Equal(1, kinds["ledger"].GetProperty("days").GetInt32());
        Assert.Equal(1, kinds["metrics"].GetProperty("days").GetInt32());
        var lost = plan.GetProperty("lost");
        Assert.Equal(2, lost.GetProperty("results").GetInt32());
        Assert.Equal(1, lost.GetProperty("human_labels").GetInt32());
        Assert.Equal(1, lost.GetProperty("labels_not_exported").GetInt32());
        Assert.Equal(3, lost.GetProperty("decisions").GetInt32());
        Assert.Equal(2, lost.GetProperty("metric_rows").GetInt32());
        Assert.False(plan.GetProperty("covered_by_export").GetBoolean());

        // 아무것도 지우지 않았다.
        Assert.True(Directory.Exists(Dir("demo", "2020-01-10")));
        Assert.True(Exists("demo", "ledger", "2020-01-10"));
        Assert.True(Exists("demo", "metrics", "2020-01-10"));
        Assert.Equal(linesBefore, RetentionLines("demo"));
    }

    [Fact]
    public async Task Purge_DeletesOnlyTheChosenKindsInsideTheRange_AndLogsIt()
    {
        var client = PinClient();
        var linesBefore = RetentionLines("demo");
        SeedRecord("demo", "2021-02-05", "in1", "OK");
        SeedRecord("demo", "2021-02-20", "out1", "OK");           // 구간 밖
        SeedRecord("other", "2021-02-05", "o1", "OK");            // 다른 시나리오
        foreach (var d in new[] { "2021-02-05", "2021-02-20" })
        {
            SeedJsonl("demo", "ledger", d, 2);
            SeedJsonl("demo", "metrics", d, 2);
        }
        SeedJsonl("other", "metrics", "2021-02-05", 2);
        var request = new { from = "2021-02-01", to = "2021-02-10", kinds = new[] { "records", "metrics" } };

        var plan = await Json(await client.PostAsync(Url, Body(request)));
        var resp = await client.PostAsync(Url, Body(new
        {
            from = "2021-02-01", to = "2021-02-10", kinds = new[] { "records", "metrics" },
            dry_run = false, plan_fingerprint = plan.GetProperty("plan_fingerprint").GetString(),
        }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var done = await Json(resp);
        Assert.True(done.GetProperty("purged").GetBoolean());
        Assert.False(done.GetProperty("dry_run").GetBoolean());
        // 지운 것: 구간 안 records · metrics.
        Assert.False(Directory.Exists(Dir("demo", "2021-02-05")));
        Assert.False(Exists("demo", "metrics", "2021-02-05"));
        // 남은 것: 고르지 않은 부류(ledger) · 구간 밖 · 다른 시나리오.
        Assert.True(Exists("demo", "ledger", "2021-02-05"));
        Assert.True(Directory.Exists(Dir("demo", "2021-02-20")));
        Assert.True(Exists("demo", "metrics", "2021-02-20"));
        Assert.True(Exists("demo", "ledger", "2021-02-20"));
        Assert.True(Directory.Exists(Dir("other", "2021-02-05")));
        Assert.True(Exists("other", "metrics", "2021-02-05"));
        // 삭제 사실이 남는다 — 한 줄, 끝까지 마쳤다고.
        var lines = File.ReadAllLines(Path.Combine(_factory.DataPath, "demo", "retention.jsonl"));
        Assert.Equal(linesBefore + 1, lines.Length);
        var rec = JsonDocument.Parse(lines[^1]).RootElement;
        Assert.True(rec.GetProperty("completed").GetBoolean());
        Assert.Equal("2021-02-01", rec.GetProperty("from").GetString());
        Assert.Equal(plan.GetProperty("plan_fingerprint").GetString(), rec.GetProperty("plan_fingerprint").GetString());
    }

    [Fact]
    public async Task Purge_RequiresTheFingerprintOfThePlanItWillExecute()
    {
        var client = PinClient();
        var linesBefore = RetentionLines("demo");
        SeedRecord("demo", "2022-03-05", "p1", "OK");
        var range = new { from = "2022-03-01", to = "2022-03-31", kinds = new[] { "records" } };

        // 지문 없이.
        var none = await client.PostAsync(Url, Body(new { range.from, range.to, range.kinds, dry_run = false }));
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        // 엉뚱한 지문.
        var wrong = await client.PostAsync(Url, Body(new
        { range.from, range.to, range.kinds, dry_run = false, plan_fingerprint = new string('0', 64) }));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        // 계획을 본 뒤 구간 안이 달라지면(라벨 하나가 더해짐) 그 지문으로는 지워지지 않는다.
        var plan = await Json(await client.PostAsync(Url, Body(range)));
        SeedRecord("demo", "2022-03-06", "p2", "NG");
        var stale = await client.PostAsync(Url, Body(new
        { range.from, range.to, range.kinds, dry_run = false, plan_fingerprint = plan.GetProperty("plan_fingerprint").GetString() }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True(Directory.Exists(Dir("demo", "2022-03-05")));
        Assert.True(Directory.Exists(Dir("demo", "2022-03-06")));
        Assert.Equal(linesBefore, RetentionLines("demo"));
    }

    [Fact]
    public async Task FingerprintOfOneSelection_DoesNotAuthorizeAnother()
    {
        var client = PinClient();
        SeedRecord("demo", "2023-04-05", "q1", "OK");
        SeedJsonl("demo", "metrics", "2023-04-05", 1);
        var plan = await Json(await client.PostAsync(Url,
            Body(new { from = "2023-04-01", to = "2023-04-30", kinds = new[] { "metrics" } })));

        // metrics 계획의 지문으로 records 를 지우려 한다.
        var resp = await client.PostAsync(Url, Body(new
        {
            from = "2023-04-01", to = "2023-04-30", kinds = new[] { "records" },
            dry_run = false, plan_fingerprint = plan.GetProperty("plan_fingerprint").GetString(),
        }));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.True(Directory.Exists(Dir("demo", "2023-04-05")));
    }

    [Theory]
    [InlineData("2024-05-10", "2024-05-01", "records")]                 // from > to
    [InlineData("2024-05-01", "2024-05-31", "")]                        // 부류 없음
    [InlineData("2024-05-01", "2024-05-31", "everything")]              // 모르는 부류
    [InlineData("2024-13-01", "2024-13-31", "records")]                 // 없는 날짜
    [InlineData("2024-05-01", "../2024-05-31", "records")]              // 경로 주입
    [InlineData("2000-01-01", "2024-05-31", "records")]                 // 구간이 너무 길다
    public async Task InvalidRequests_AreRejected_WithoutTouchingAnything(string from, string to, string kind)
    {
        var client = PinClient();
        SeedRecord("demo", "2024-05-05", "r1", "OK");
        var kinds = kind.Length == 0 ? Array.Empty<string>() : [kind];

        var resp = await client.PostAsync(Url, Body(new { from, to, kinds, dry_run = false, plan_fingerprint = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.True(Directory.Exists(Dir("demo", "2024-05-05")));
    }

    [Fact]
    public async Task TodayAndLater_AreNeverInTheRange()
    {
        var client = PinClient();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        foreach (var to in new[] { today, tomorrow })
        {
            var resp = await client.PostAsync(Url, Body(new { from = "2025-01-01", to, kinds = new[] { "records" } }));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        // 어제까지는 받는다.
        var ok = await client.PostAsync(Url, Body(new { from = "2025-01-01", to = yesterday, kinds = new[] { "records" } }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task UnknownScenario_Is404()
    {
        var resp = await PinClient().PostAsync("/api/u-vision/scenarios/nope/retention/purge",
            Body(new { from = "2019-01-01", to = "2019-01-31", kinds = new[] { "records" } }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task CoveredByExport_ReflectsWhetherEveryLabelWasExported()
    {
        var client = PinClient();
        SeedRecord("demo", "2026-01-05", "e1", "OK");
        SeedRecord("demo", "2026-01-05", "e2", "NG");
        var export = await client.PostAsync("/api/u-vision/datasets/export?scenario_id=demo", null);
        Assert.Equal(HttpStatusCode.Created, export.StatusCode);
        var range = new { from = "2026-01-01", to = "2026-01-31", kinds = new[] { "records" } };

        var covered = await Json(await client.PostAsync(Url, Body(range)));
        Assert.True(covered.GetProperty("covered_by_export").GetBoolean());
        Assert.Equal(0, covered.GetProperty("lost").GetProperty("labels_not_exported").GetInt32());

        // export 뒤에 라벨이 더해지면 그 라벨은 어느 export 에도 없다.
        SeedRecord("demo", "2026-01-06", "e3", "NG");
        var notCovered = await Json(await client.PostAsync(Url, Body(range)));
        Assert.False(notCovered.GetProperty("covered_by_export").GetBoolean());
        Assert.Equal(1, notCovered.GetProperty("lost").GetProperty("labels_not_exported").GetInt32());
    }

    [Fact]
    public async Task AfterPurge_TheDaysAreGoneFromTheResultList_AndTheLabelList()
    {
        var client = PinClient();
        SeedRecord("demo", "2018-06-05", "g1", "OK");
        // 삭제 전에 한 번 읽어 라벨 색인을 데워 둔다 — 지운 뒤 낡은 색인이 남으면 안 된다.
        var before = await Json(await client.GetAsync("/api/u-vision/results/labels?scenario_id=demo&date=2018-06-05"));
        Assert.Equal(1, before.GetArrayLength());
        var range = new { from = "2018-06-01", to = "2018-06-30", kinds = new[] { "records" } };
        var plan = await Json(await client.PostAsync(Url, Body(range)));

        var purge = await client.PostAsync(Url, Body(new
        {
            range.from, range.to, range.kinds, dry_run = false,
            plan_fingerprint = plan.GetProperty("plan_fingerprint").GetString(),
        }));
        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);

        var dates = await Json(await client.GetAsync("/api/u-vision/results/dates?scenario_id=demo"));
        Assert.DoesNotContain(dates.EnumerateArray(), d => d.GetString() == "2018-06-05");
        var after = await Json(await client.GetAsync("/api/u-vision/results/labels?scenario_id=demo&date=2018-06-05"));
        Assert.Equal(0, after.GetArrayLength());
    }

    [Fact]
    public async Task Purge_WithdrawsOpenQuestionsAboutThePurgedPhotos_AndLeavesTheOthers()
    {
        var client = PinClient();
        var store = _factory.Services.GetRequiredService<UVision.Api.Storage.IQuestionStore>();
        SeedRecord("demo", "2017-07-05", "h1");
        SeedRecord("demo", "2017-08-05", "h2");
        async Task<StoredQuestion> Ask(string date, string image)
        {
            var q = new StoredQuestion
            {
                QuestionId = Guid.NewGuid().ToString("N"), ScenarioId = "demo", Date = date, ImageId = image,
                Form = QuestionForm.Single, Stream = LabelStream.Learn, Reason = QuestionReason.RandomFloor,
                CreatedAt = "2017-07-06T00:00:00.0000000Z", AskedTo = "dev", State = QuestionState.Open,
            };
            await store.CreateAsync(q);
            return q;
        }
        var inside = await Ask("2017-07-05", "h1");
        var outside = await Ask("2017-08-05", "h2");
        var range = new { from = "2017-07-01", to = "2017-07-31", kinds = new[] { "records" } };

        var plan = await Json(await client.PostAsync(Url, Body(range)));
        Assert.Equal(1, plan.GetProperty("lost").GetProperty("open_questions").GetInt32());
        var purge = await client.PostAsync(Url, Body(new
        {
            range.from, range.to, range.kinds, dry_run = false,
            plan_fingerprint = plan.GetProperty("plan_fingerprint").GetString(),
        }));

        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        Assert.Equal(QuestionState.Withdrawn, (await store.ReadAsync("demo", inside.QuestionId))!.State);
        Assert.Equal(QuestionState.Open, (await store.ReadAsync("demo", outside.QuestionId))!.State);
    }
}
