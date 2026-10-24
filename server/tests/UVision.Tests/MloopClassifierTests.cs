using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Ml;
using UVision.Api.Services.Mloop;
using UVision.Api.Services.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// MloopClassifier 계약 테스트(fake HTTP handler) — HoneAI <c>IMLoopClient.PredictImagesAsync</c> 를 거친 MLoop.API /predict 계약(0.45.0 소스 확인 — 이미지 내용 입력) 대비
/// 요청 구성·응답 파싱·에러 처리를 검증. 실 mloop serve 왕복은 이 테스트 밖이다.
/// </summary>
public class MloopClassifierTests
{
    /// <summary>요청을 캡처하고 정해진 응답을 돌려주는 테스트용 핸들러.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static readonly byte[] Image = [1, 2, 3, 4];

    private static MloopClassifier Build(FakeHandler handler, MlOptions? options = null)
    {
        return new MloopClassifier(MloopClient.Create("http://mloop.test", 30, "jwt-abc", handler), options ?? new MlOptions
        {
            Provider = "mloop", Endpoint = "http://mloop.test", Model = "default", Token = "jwt-abc",
        });
    }

    private const string OkResponse = """
        {
          "modelName": "default",
          "task": "image-classification",
          "count": 1,
          "predictions": [
            { "predictedLabel": "ng", "probabilities": { "class_0": 0.18, "class_1": 0.82 }, "score": null, "confidence": 0.82 }
          ],
          "warnings": []
        }
        """;

    [Fact]
    public async Task Classify_ParsesPredictedLabel_AndConfidence()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, OkResponse);
        var result = await Build(handler).ClassifyAsync(Image, "demo");

        Assert.Equal("ng", result.Label);
        Assert.Equal(0.82, result.Confidence, precision: 3); // 서버 confidence
        Assert.Equal(2, result.Scores.Count);
        Assert.Equal(0.82, result.Scores["class_1"], precision: 3);
    }

    [Fact]
    public async Task Classify_SendsImageContentsAsBase64_WithBearer_ToPredictEndpoint()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, OkResponse);
        await Build(handler).ClassifyAsync(Image, "demo");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("predict?name=default", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("jwt-abc", handler.LastRequest.Headers.Authorization.Parameter);
        // 경로 행이 아니라 {"images": ["<base64>"]} — mloop serve 가 다른 호스트여도 된다.
        using var body = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var images = body.RootElement.GetProperty("images");
        Assert.Equal(1, images.GetArrayLength());
        Assert.Equal(Image, Convert.FromBase64String(images[0].GetString()!));
    }

    [Fact]
    public async Task Classify_Throws_OnErrorStatus()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, """{"error":"no model"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).ClassifyAsync(Image, "demo"));
    }

    [Fact]
    public async Task Classify_Throws_OnEmptyPredictions()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"predictions":[]}""");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).ClassifyAsync(Image, "demo"));
    }

    [Fact]
    public async Task Classify_MissingServerConfidence_IsZero_NotDerived()
    {
        // 0.45.0+ 는 이미지 분류에 confidence 를 늘 준다 — 없으면 확률에서 파생하지 않고 0(신뢰도 주장 없음).
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"predictions":[{"predictedLabel":"ng","probabilities":{"ng":0.9,"ok":0.1}}]}""");
        var result = await Build(handler).ClassifyAsync(Image, "demo");
        Assert.Equal(0.0, result.Confidence);
        Assert.Equal(0.9, result.Scores["ng"], precision: 3); // 원시 확률은 그대로 보존
    }

    // MLoop confidence 계약 소비 — 서버의 정규화 confidence ∈ [0,1] 가 단일 권위 --------

    [Fact]
    public async Task Classify_PrefersServerConfidence_OverDerivedMaxProb()
    {
        // 파생 max-prob(0.82)와 다른 값(0.6)으로 서버 confidence 우선을 증명.
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"predictions":[{"predictedLabel":"ng","probabilities":{"ok":0.18,"ng":0.82},"confidence":0.6}]}""");
        var result = await Build(handler).ClassifyAsync(Image, "demo");
        Assert.Equal(0.6, result.Confidence, precision: 3);
    }

    [Fact]
    public async Task Classify_ClampsServerConfidence_AtBoundary()
    {
        // 서버 confidence 도 trust boundary(A3) 정규화 대상 — 1.5 → 1.0 (파생 0.4 로 폴백하지 않음).
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"predictions":[{"predictedLabel":"ng","probabilities":{"ng":0.4,"ok":0.3},"confidence":1.5}]}""");
        var result = await Build(handler).ClassifyAsync(Image, "demo");
        Assert.Equal(1.0, result.Confidence);
    }

    [Fact]
    public async Task Classify_NonFiniteServerConfidence_BecomesZero_NotFallback()
    {
        // 1e999 → STJ 가 double.PositiveInfinity 로 파싱. 서버가 confidence 를 '줬으나 오염'이면
        // 파생 폴백(0.9)이 아니라 0 — 오염 응답의 신뢰도 주장을 신뢰하지 않는다.
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"predictions":[{"predictedLabel":"ng","probabilities":{"ng":0.9,"ok":0.1},"confidence":1e999}]}""");
        var result = await Build(handler).ClassifyAsync(Image, "demo");
        Assert.Equal(0.0, result.Confidence);
    }

    // B1: resolver 결선 계약 테스트 ------------------------------------

    [Fact]
    public async Task Classify_UsesResolvedModelNameAndStampsVersion()
    {
        // active 바인딩(v1 → "chromate-special")을 가진 레지스트리.
        var dataPath = Path.Combine(Path.GetTempPath(), "uv-mloop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(new StorageOptions { DataPath = dataPath }, AppContext.BaseDirectory);
            var registry = new FileModelRegistry(paths);
            await registry.RegisterAsync("chromate", new ModelRegistration { ModelName = "chromate-special" });
            await registry.PromoteAsync("chromate", "v1", "x");
            var resolver = new ModelBindingResolver(registry, NullLogger<ModelBindingResolver>.Instance);

            // /predict?name= 의 name 을 포착하는 fake handler.
            string? capturedQuery = null;
            var handler = new CapturingHandler(req =>
            {
                capturedQuery = req.RequestUri!.Query;
                return """{"predictions":[{"predictedLabel":"ng","probabilities":{"ng":0.9,"ok":0.1},"confidence":0.9}]}""";
            });
            var classifier = new MloopClassifier(
                MloopClient.Create("http://fake", 30, null, handler), new MlOptions { Provider = "mloop", Model = "default" }, resolver);

            var result = await classifier.ClassifyAsync(new byte[] { 1, 2, 3 }, "chromate");

            // 전역 "default" 가 아닌 active 바인딩 사용
            Assert.Contains("name=chromate-special", capturedQuery);
            Assert.Equal("v1", result.ModelVersion);
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task Classify_NoBinding_FallsBackToGlobalModel()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "uv-mloop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(new StorageOptions { DataPath = dataPath }, AppContext.BaseDirectory);
            var resolver = new ModelBindingResolver(new FileModelRegistry(paths), NullLogger<ModelBindingResolver>.Instance);

            string? capturedQuery = null;
            var handler = new CapturingHandler(req =>
            {
                capturedQuery = req.RequestUri!.Query;
                return """{"predictions":[{"predictedLabel":"ok","probabilities":{"ok":0.8,"ng":0.2},"confidence":0.8}]}""";
            });
            var classifier = new MloopClassifier(
                MloopClient.Create("http://fake", 30, null, handler), new MlOptions { Provider = "mloop", Model = "default" }, resolver);

            var result = await classifier.ClassifyAsync(new byte[] { 1 }, "chromate");

            Assert.Contains("name=default", capturedQuery); // 미등록 → 전역 폴백
            Assert.Null(result.ModelVersion);
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    /// <summary>요청을 캡처하고 응답 본문을 동적으로 결정하는 테스트용 핸들러.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _respond;
        public CapturingHandler(Func<HttpRequestMessage, string> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = _respond(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
