using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>기준 이미지 갤러리(S-D) — 업로드/목록/서빙/삭제 왕복, label 거부, NG 레이블 orphan, degrade.</summary>
public class ReferenceTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;

    public ReferenceTests(PinFactory factory) => _factory = factory;

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        return client;
    }

    private static MultipartFormDataContent UploadForm(
        byte[] bytes, string contentType, string label, string? ngLabel = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "image", "ref" + (contentType == "image/png" ? ".png" : ".jpg"));
        form.Add(new StringContent(label), "label");
        if (ngLabel is not null)
            form.Add(new StringContent(ngLabel), "ng_label");
        return form;
    }

    [Fact]
    public async Task Upload_List_Serve_Delete_RoundTrips()
    {
        var admin = Admin();
        var anon = _factory.CreateClient();

        // 업로드(OK 기준, png).
        var up = await admin.PostAsync("/api/scenarios/demo/references", UploadForm(Png, "image/png", "ok"));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var refId = (await up.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ref_id").GetString();

        // 목록(무인증).
        var list = await (await anon.GetAsync("/api/scenarios/demo/references"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.EnumerateArray(), r => r.GetProperty("ref_id").GetString() == refId);

        // 이미지 서빙(무인증) — content-type 보존.
        var img = await anon.GetAsync($"/api/scenarios/demo/references/ok/{refId}");
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal("image/png", img.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await img.Content.ReadAsByteArrayAsync());

        // 삭제(PIN).
        var del = await admin.DeleteAsync($"/api/scenarios/demo/references/ok/{refId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var after = await (await anon.GetAsync("/api/scenarios/demo/references"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(after.EnumerateArray(), r => r.GetProperty("ref_id").GetString() == refId);
    }

    [Fact]
    public async Task Upload_Ng_StoresLabel_AndDeleteRemovesOrphan()
    {
        var admin = Admin();

        var up = await admin.PostAsync(
            "/api/scenarios/demo/references", UploadForm(Png, "image/png", "ng", "솔더 브릿지"));
        var refId = (await up.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("ref_id").GetString();

        // NG 레이블이 목록에 결합된다.
        var list = await (await admin.GetAsync("/api/scenarios/demo/references"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var entry = list.EnumerateArray().First(r => r.GetProperty("ref_id").GetString() == refId);
        Assert.Equal("솔더 브릿지", entry.GetProperty("ng_label").GetString());

        // scenario.json 에 기록됐는지(GET 시나리오).
        var scenario = await (await admin.GetAsync("/api/scenarios/demo"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(scenario.GetProperty("ng_labels").TryGetProperty(refId!, out _));

        // 삭제 후 ng_labels orphan 제거.
        await admin.DeleteAsync($"/api/scenarios/demo/references/ng/{refId}");
        var after = await (await admin.GetAsync("/api/scenarios/demo"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(after.GetProperty("ng_labels").TryGetProperty(refId!, out _));
    }

    [Fact]
    public async Task Upload_InvalidLabel_Returns400()
    {
        var resp = await Admin().PostAsync(
            "/api/scenarios/demo/references", UploadForm(Png, "image/png", "maybe"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutPin_Returns401()
    {
        var resp = await _factory.CreateClient().PostAsync(
            "/api/scenarios/demo/references", UploadForm(Png, "image/png", "ok"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Inspect_DegradesGracefully_WhenReferenceLoadFails()
    {
        // 기준이미지 로드가 실패해도 inspect 는 200(few-shot 없이 진행) — must-succeed 아님.
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
                s.AddSingleton<IReferenceStore, ThrowingReferenceStore>())).CreateClient();

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");

        var resp = await client.PostAsync("/api/inspect", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private sealed class ThrowingReferenceStore : IReferenceStore
    {
        public Task<string> SaveAsync(string s, ReferenceLabel l, ReadOnlyMemory<byte> i, string e,
            CancellationToken ct = default) => throw new IOException("test");

        public Task<IReadOnlyList<ReferenceInfo>> ListAsync(string s, CancellationToken ct = default) =>
            throw new IOException("test");

        public Task<ReferenceBytes?> ReadAsync(string s, ReferenceLabel l, string r,
            CancellationToken ct = default) => throw new IOException("test");

        public Task<bool> DeleteAsync(string s, ReferenceLabel l, string r,
            CancellationToken ct = default) => throw new IOException("test");

        public Task<IReadOnlyList<ReferenceImage>> LoadImagesAsync(
            string s, IReadOnlyDictionary<string, string> ng, int max, CancellationToken ct = default) =>
            throw new IOException("기준이미지 로드 실패(테스트)");
    }
}
