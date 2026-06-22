using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Ml;
using UVision.Api.Services.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// FW-2 — MloopClassifier 계약 테스트(fake HTTP handler). MLoop.API /predict 계약(소스 확인) 대비
/// 요청 구성·응답 파싱·에러 처리를 검증. 실 mloop serve 왕복은 FW-3 게이트(UNVERIFIED).
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
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://mloop.test/") };
        return new MloopClassifier(http, options ?? new MlOptions
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
            { "predictedLabel": "ng", "probabilities": { "class_0": 0.18, "class_1": 0.82 }, "score": null }
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
        Assert.Equal(0.82, result.Confidence, precision: 3); // argmax 확률
        Assert.Equal(2, result.Scores.Count);
        Assert.Equal(0.82, result.Scores["class_1"], precision: 3);
    }

    [Fact]
    public async Task Classify_SendsImagePathRow_WithBearer_ToPredictEndpoint()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, OkResponse);
        await Build(handler).ClassifyAsync(Image, "demo");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("predict?name=default", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("jwt-abc", handler.LastRequest.Headers.Authorization.Parameter);
        // 기본 이미지 컬럼명(ImagePath)으로 경로를 보낸다.
        Assert.Contains("ImagePath", handler.LastRequestBody);
    }

    [Fact]
    public async Task Classify_RespectsConfiguredImageColumn()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, OkResponse);
        var opts = new MlOptions { Endpoint = "http://mloop.test", Model = "default", ImageColumn = "ImageSource" };
        await Build(handler, opts).ClassifyAsync(Image, "demo");

        Assert.Contains("ImageSource", handler.LastRequestBody);
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
    public async Task Classify_DeletesTempFile()
    {
        // 임시 파일이 남지 않는다(요청 전후 temp 디렉토리의 uvision-ml-* 수 불변).
        var handler = new FakeHandler(HttpStatusCode.OK, OkResponse);
        var before = Directory.GetFiles(Path.GetTempPath(), "uvision-ml-*").Length;
        await Build(handler).ClassifyAsync(Image, "demo");
        var after = Directory.GetFiles(Path.GetTempPath(), "uvision-ml-*").Length;
        Assert.Equal(before, after);
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
                return """{"predictions":[{"predictedLabel":"ng","probabilities":{"ng":0.9,"ok":0.1}}]}""";
            });
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake/") };
            var classifier = new MloopClassifier(
                http, new MlOptions { Provider = "mloop", Model = "default" }, resolver);

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
                return """{"predictions":[{"predictedLabel":"ok","probabilities":{"ok":0.8,"ng":0.2}}]}""";
            });
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake/") };
            var classifier = new MloopClassifier(
                http, new MlOptions { Provider = "mloop", Model = "default" }, resolver);

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
