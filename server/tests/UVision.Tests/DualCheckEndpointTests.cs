using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// ③ 2중체크 wire 경로 e2e — ML 활성 시 additive 필드 직렬화·영속 왕복,
/// ML 비활성(기본) 시 응답 byte-freeze(필드 미출현).
/// mock VLM·mock ML 은 독립 바이트-해시 분기라 일치/불일치 모두 가능 → 엔드포인트는 불변식
/// (임계 0 → requires_review ⟺ 불일치)만 단언하고, 구체 비교 로직은 evaluator 단위테스트가 검증.
/// </summary>
public class DualCheckEndpointTests
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent Form(string scenarioId)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        return form;
    }

    [Fact]
    public async Task MlEnabled_Response_HasDualCheckFields()
    {
        using var factory = new MlEnabledFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("ml", out var ml));
        Assert.True(ml.GetProperty("label").GetString() is "ok" or "ng");
        Assert.InRange(ml.GetProperty("confidence").GetDouble(), 0.0, 1.0);

        Assert.True(json.TryGetProperty("agreement", out var agreement));
        Assert.True(agreement.ValueKind is JsonValueKind.True or JsonValueKind.False);

        Assert.True(json.TryGetProperty("requires_review", out var review));
        Assert.True(review.ValueKind is JsonValueKind.True or JsonValueKind.False);

        // 불변식: 임계 0(기본) → requires_review 는 정확히 VLM·ML 불일치일 때만 true.
        Assert.Equal(!agreement.GetBoolean(), review.GetBoolean());
    }

    [Fact]
    public async Task MlEnabled_Persisted_RoundTripsDualCheck()
    {
        using var factory = new MlEnabledFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        var posted = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = posted.GetProperty("image_id").GetString();

        // 저장된 레코드(StoredResult)에도 2중체크 필드가 영속화돼야 한다.
        var list = await client.GetFromJsonAsync<JsonElement>(
            "/api/u-vision/results?scenario_id=demo");
        var record = list.EnumerateArray()
            .First(r => r.GetProperty("image_id").GetString() == imageId);

        Assert.True(record.TryGetProperty("ml", out var ml));
        Assert.True(ml.GetProperty("label").GetString() is "ok" or "ng");
        Assert.True(record.TryGetProperty("agreement", out _));
        Assert.True(record.TryGetProperty("requires_review", out _));
    }

    [Fact]
    public async Task MlDisabled_Response_OmitsDualCheckFields_ByteFreeze()
    {
        // 기본 팩토리 = ML none → ② 이전과 동일하게 additive 필드가 전혀 나오지 않아야 한다.
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("ml", out _));
        Assert.False(json.TryGetProperty("agreement", out _));
        Assert.False(json.TryGetProperty("requires_review", out _));
    }

    [Fact]
    public async Task MlDisabled_Persisted_OmitsDualCheckFields()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        var posted = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = posted.GetProperty("image_id").GetString();

        var list = await client.GetFromJsonAsync<JsonElement>(
            "/api/u-vision/results?scenario_id=demo");
        var record = list.EnumerateArray()
            .First(r => r.GetProperty("image_id").GetString() == imageId);

        Assert.False(record.TryGetProperty("ml", out _));
        Assert.False(record.TryGetProperty("agreement", out _));
        Assert.False(record.TryGetProperty("requires_review", out _));
    }
}
