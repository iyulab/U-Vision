using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 선별(<c>classification</c>)의 기준 이미지 — 클래스 폴더(<c>references/{class}</c>)에 두고, 응답의 <c>label</c> 은 클래스의 OK/NG 보기 ·
/// <c>class</c> 가 본체이며, VLM 입력에는 클래스마다 고르게 실려 프롬프트 표지가 클래스가 된다. 검사 시나리오의 기준 이미지는 그대로다.
/// </summary>
public class ClassificationReferenceTests : IClassFixture<PinFactory>
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private readonly PinFactory _factory;

    public ClassificationReferenceTests(PinFactory factory) => _factory = factory;

    private HttpClient Admin(PinFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        return client;
    }

    private async Task<string> CreateScenarioAsync(string type = "classification")
    {
        object body = type == "classification"
            ? new
            {
                name = "품번 구분", type,
                classes = new object[]
                {
                    new { id = "P-100", label = "품번 100", description = "각진 브래킷", settleable = true },
                    new { id = "P-200" },
                    new { id = "REJECT", must_catch = true },
                },
            }
            : new { name = "검사" };
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(body));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString()!;
    }

    private static MultipartFormDataContent Upload(string label, string? ngLabel = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "ref.jpg");
        form.Add(new StringContent(label), "label");
        if (ngLabel is not null) form.Add(new StringContent(ngLabel), "ng_label");
        return form;
    }

    [Fact]
    public async Task UploadListServeDelete_WorkPerClass_AndLabelIsTheMustCatchView()
    {
        var id = await CreateScenarioAsync();

        var ok = await Admin().PostAsync($"/api/u-vision/scenarios/{id}/references", Upload("p-100")); // 철자는 선언대로
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var created = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("P-100", created.GetProperty("class").GetString());
        Assert.Equal(0, created.GetProperty("label").GetInt32()); // 꼭 잡을 클래스가 아니다 → 보기는 ok(0)

        var reject = await Admin().PostAsync($"/api/u-vision/scenarios/{id}/references", Upload("REJECT"));
        var rejectBody = await reject.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, rejectBody.GetProperty("label").GetInt32()); // must_catch → ng(1)
        var refId = created.GetProperty("ref_id").GetString();

        var list = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}/references");
        Assert.Equal(["P-100", "REJECT"], list.EnumerateArray().Select(r => r.GetProperty("class").GetString()));

        var served = await _factory.CreateClient().GetAsync($"/api/u-vision/scenarios/{id}/references/P-100/{refId}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(Jpeg, await served.Content.ReadAsByteArrayAsync());

        var del = await Admin().DeleteAsync($"/api/u-vision/scenarios/{id}/references/p-100/{refId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var after = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}/references");
        Assert.Equal(["REJECT"], after.EnumerateArray().Select(r => r.GetProperty("class").GetString()));
    }

    [Theory]
    [InlineData("P-999")]   // 선언하지 않은 클래스
    [InlineData("ok")]      // 선별에 OK 라는 클래스가 없다 — 옛 ok|ng 폴더로 새지 않는다
    [InlineData("ng")]
    public async Task UploadWithAnUndeclaredClass_IsRefused_AndNothingIsWrittenToTheLegacyFolders(string label)
    {
        var id = await CreateScenarioAsync();

        var resp = await Admin().PostAsync($"/api/u-vision/scenarios/{id}/references", Upload(label));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("클래스 id", await resp.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(Path.Combine(_factory.DataPath, id, "references", "ok")));
    }

    [Fact]
    public async Task NgLabel_IsAnInspectionConcept_AndRefusedForClassification()
    {
        var id = await CreateScenarioAsync();

        var resp = await Admin().PostAsync($"/api/u-vision/scenarios/{id}/references", Upload("REJECT", ngLabel: "scratch"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("ng_label", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InspectionScenario_ReferencesAreUnchanged()
    {
        var id = await CreateScenarioAsync(type: "inspection");

        var created = await Admin().PostAsync($"/api/u-vision/scenarios/{id}/references", Upload("ng", ngLabel: "scratch"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("class", out _));          // 응답 모양이 그대로 — class 는 선별에서만 나온다
        Assert.Equal("scratch", body.GetProperty("ng_label").GetString());

        var bad = await Admin().PostAsync($"/api/u-vision/scenarios/{id}/references", Upload("P-100"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("ok|ng", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Inspect_SendsTheClassReferences_TaggedByClass()
    {
        using var baseFactory = new PinFactory();
        var admin = Admin(baseFactory);
        var create = await admin.PostAsync("/api/u-vision/scenarios", JsonContent.Create(new
        {
            name = "품번 구분", type = "classification", reference_cap = 1,
            classes = new object[] { new { id = "P-100", label = "품번 100" }, new { id = "P-200" } },
        }));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString()!;
        foreach (var label in new[] { "P-100", "P-100", "P-200" })     // P-100 은 두 장이지만 reference_cap=1 이라 한 장만
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"/api/u-vision/scenarios/{id}/references", Upload(label))).StatusCode);

        var vlm = new RecordingVlm();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IVlmProvider>(vlm)));
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(id), "scenario_id");
        var resp = await factory.CreateClient().PostAsync("/api/u-vision/v2/inspect", form);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // 클래스마다 한 장씩 — 표지는 OK/NG 가 아니라 클래스다.
        Assert.Equal(["[P-100(품번 100) 기준]", "[P-200 기준]"],
            vlm.Seen!.Select(r => VlmPrompt.ReferenceTag(r, null, vlm.SeenClasses)));
    }

    private sealed class RecordingVlm : IVlmProvider
    {
        public IReadOnlyList<ReferenceImage>? Seen { get; private set; }
        public IReadOnlyList<ScenarioClass>? SeenClasses { get; private set; }
        public string Name => "recording";

        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default)
        {
            Seen = scenario.References;
            SeenClasses = scenario.Classes;
            return Task.FromResult(new InspectionResult { Verdict = Verdict.OK, Findings = "", Confidence = 0.9, Class = "P-100" });
        }
    }
}
