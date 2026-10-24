using System.Net;
using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Mloop;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// MloopImageEmbedder 계약 테스트(fake HTTP handler) — HoneAI <c>IMLoopClient.EmbedAsync</c> 를 거친 MLoop <c>POST /embed</c> 계약(0.45.0 소스 확인) 대비 요청 구성 ·
/// 응답 파싱 · 오류. 실 <c>mloop serve</c> + 인코더 왕복은 이 테스트 밖이다.
/// </summary>
public class MloopImageEmbedderTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static MloopImageEmbedder Build(FakeHandler handler, string token = "jwt-abc") =>
        new(MloopClient.Create("http://mloop.test", 30, token, handler),
            new MemoryOptions { Provider = "mloop", Endpoint = "http://mloop.test", Encoder = "dinov2 small", Token = token });

    private const string OkResponse = """
        {
          "encoder": { "id": "dinov2-small", "version": "1", "sha256": "ABCDEF0123456789ff", "dimension": 3, "normalized": true },
          "vectors": [[1, 0, 0], [0, 0.6, 0.8]]
        }
        """;

    [Fact]
    public async Task Embed_SendsBase64Contents_EncoderQuery_AndBearer()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, OkResponse);
        await Build(handler).EmbedAsync([new byte[] { 1, 2, 3 }, new byte[] { 255 }]);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://mloop.test/embed?encoder=dinov2%20small", handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("jwt-abc", handler.LastRequest.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(["AQID", "/w=="], body.RootElement.GetProperty("images").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Embed_ParsesEncoderIdentity_AndVectorsInOrder()
    {
        var result = await Build(new FakeHandler(HttpStatusCode.OK, OkResponse)).EmbedAsync([new byte[] { 1 }, new byte[] { 2 }]);

        Assert.Equal(new EncoderIdentity("dinov2-small", "1", "ABCDEF0123456789ff", 3, true), result.Encoder);
        Assert.Equal("dinov2-small-abcdef012345", result.Encoder.Key);
        Assert.Equal([1f, 0f, 0f], result.Vectors[0]);
        Assert.Equal([0f, 0.6f, 0.8f], result.Vectors[1]);
    }

    [Fact]
    public async Task Embed_WithoutToken_SendsNoAuthorization()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, OkResponse);
        await Build(handler, token: "").EmbedAsync([new byte[] { 1 }, new byte[] { 2 }]);

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task Embed_NormalizesVectors_WhenServerSaysNotNormalized()
    {
        const string raw = """{ "encoder": { "id": "e", "version": "1", "sha256": null, "dimension": 2, "normalized": false }, "vectors": [[3, 4]] }""";
        var result = await Build(new FakeHandler(HttpStatusCode.OK, raw)).EmbedAsync([new byte[] { 1 }]);

        Assert.Equal([0.6f, 0.8f], result.Vectors[0]);
        Assert.True(result.Encoder.Normalized);
        Assert.Equal("e-1", result.Encoder.Key);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"Image 0 is not an image.","index":0}""")]
    [InlineData(HttpStatusCode.Conflict, """{"error":"Encoder 'x' is not installed: mloop encoder add …"}""")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, """{"error":"65 images in one request; at most 64."}""")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    public async Task Embed_NonSuccess_ThrowsWithStatusAndBody(HttpStatusCode status, string body)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(new FakeHandler(status, body)).EmbedAsync([new byte[] { 1 }]));

        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.Contains(body, ex.Message);
    }

    [Theory]
    [InlineData("""{ "encoder": { "id": "e", "version": "1", "dimension": 2, "normalized": true }, "vectors": [[1, 0]] }""")]       // 2 장에 벡터 1 개
    [InlineData("""{ "encoder": { "id": "e", "version": "1", "dimension": 3, "normalized": true }, "vectors": [[1, 0], [0, 1]] }""")] // 차원 불일치
    [InlineData("""{ "vectors": [[1, 0], [0, 1]] }""")]                                                                              // encoder 없음
    public async Task Embed_MalformedResponse_Throws(string body) =>
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(new FakeHandler(HttpStatusCode.OK, body)).EmbedAsync([new byte[] { 1 }, new byte[] { 2 }]));

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task Embed_RejectsBatchOutsideOneTo64_WithoutCalling(int count)
    {
        var handler = new FakeHandler(HttpStatusCode.OK, OkResponse);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Build(handler).EmbedAsync([.. Enumerable.Range(0, count).Select(_ => (ReadOnlyMemory<byte>)new byte[] { 1 })]));
        Assert.Null(handler.LastRequest);
    }
}
