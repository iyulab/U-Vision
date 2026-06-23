using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// ④-B 오라클 byte-identical 회귀 가드.
/// Oracle <c>Provider=none</c>(기본) 환경에서 <c>/api/inspect</c> 응답이 오라클 도입 전과
/// 동일 스키마를 유지하는지(오라클 필드가 wire 에 새지 않는지) 고정한다.
/// <para>
/// 오라클은 out-of-band 파일 기반 스윕이므로 inspect 응답 키가 늘어서는 안 된다.
/// ML none + Oracle none = 사전 ④-B 응답(verdict/findings/confidence/timestamp/image_id 만).
/// </para>
/// </summary>
public class OracleInspectRegressionTests : IClassFixture<UVisionApiFactory>
{
    private readonly UVisionApiFactory _factory;

    public OracleInspectRegressionTests(UVisionApiFactory factory) => _factory = factory;

    /// 최소 유효 JPEG — InspectTests 와 동일 바이트.
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

    /// <summary>
    /// Oracle none + ML none 환경에서 inspect 응답 키 집합이 pre-④-B 계약과 동일해야 한다.
    /// 허용 키: verdict, findings, confidence, timestamp, image_id.
    /// 금지 키: oracle 관련 모든 것(이름 무관) + ml/agreement/requires_review(ML none 이라 부재).
    /// </summary>
    [Fact]
    public async Task Inspect_OracleNone_ResponseHasNoOracleFields()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // --- 필수 키 존재 확인 (pre-④-B 계약) ---
        Assert.True(body.TryGetProperty("verdict", out var verdict),
            "verdict 키 누락 — 기본 계약 위반");
        Assert.True(verdict.GetString() is "OK" or "NG",
            "verdict 값이 OK/NG 아님");

        Assert.True(body.TryGetProperty("findings", out _),
            "findings 키 누락");
        Assert.True(body.TryGetProperty("confidence", out var conf),
            "confidence 키 누락");
        Assert.InRange(conf.GetDouble(), 0.0, 1.0);

        Assert.True(body.TryGetProperty("timestamp", out var ts),
            "timestamp 키 누락");
        Assert.False(string.IsNullOrEmpty(ts.GetString()));

        Assert.True(body.TryGetProperty("image_id", out var imageId),
            "image_id 키 누락");
        Assert.StartsWith("img_", imageId.GetString());

        // --- ML none → ML 계열 필드 모두 부재 확인 ---
        Assert.False(body.TryGetProperty("ml", out _),
            "ml 필드가 wire 에 노출됨 — ML none 이면 생략되어야 한다");
        Assert.False(body.TryGetProperty("agreement", out _),
            "agreement 필드가 wire 에 노출됨 — ML none 이면 생략되어야 한다");
        Assert.False(body.TryGetProperty("requires_review", out _),
            "requires_review 필드가 wire 에 노출됨 — ML none 이면 생략되어야 한다");

        // --- Oracle none → 오라클 관련 키 일체 부재 확인 ---
        // 오라클은 out-of-band 이므로 inspect 응답에 절대 추가되어서는 안 된다.
        Assert.False(body.TryGetProperty("oracle", out _),
            "oracle 필드 누출 — 오라클은 inspect 응답에 영향을 주지 않아야 한다");
        Assert.False(body.TryGetProperty("oracle_verdict", out _),
            "oracle_verdict 누출");
        Assert.False(body.TryGetProperty("oracle_label", out _),
            "oracle_label 누출");
        Assert.False(body.TryGetProperty("sweep", out _),
            "sweep 필드 누출");

        // --- 전체 키 집합 열거 — 예상치 못한 신규 필드 탐지 ---
        // pre-④-B 허용 키(posture 는 advisory additive nullable 이라 부재가 정상이지만 존재해도 OK).
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "verdict", "findings", "confidence", "timestamp", "image_id", "posture",
        };
        foreach (var prop in body.EnumerateObject())
        {
            Assert.True(allowed.Contains(prop.Name),
                $"예상치 않은 키 '{prop.Name}' 가 inspect 응답에 추가됨 — oracle none 이면 wire 는 pre-④-B 과 동일해야 한다");
        }
    }

    /// <summary>
    /// inspect 응답이 200 OK 이고 필수 필드가 있는 기본 smoke — oracle 코드가 경로를 망가뜨리지 않음을 확인.
    /// </summary>
    [Fact]
    public async Task Inspect_OracleNone_Returns200WithVerdict()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("verdict").GetString() is "OK" or "NG");
        Assert.StartsWith("img_", body.GetProperty("image_id").GetString());
    }
}
