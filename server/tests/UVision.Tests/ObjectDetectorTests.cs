using System.Net;
using System.Text.Json;
using SkiaSharp;
using UVision.Api.Configuration;
using UVision.Api.Services.Counting;
using UVision.Api.Services.Mloop;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 검출기 경계 테스트. mloop 구현은 fake HTTP handler 로 HoneAI <c>IMLoopClient.PredictImagesAsync</c> 를 거친다 —
/// 응답 모양은 업스트림 문서의 서술이고 실 <c>mloop serve</c> + 검출 모델의 왕복은 이 테스트 밖이다(UNVERIFIED).
/// </summary>
public class ObjectDetectorTests
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

    /// <summary>가로 200 · 세로 100 PNG — 좌표 비율 계산이 이 크기를 쓴다.</summary>
    private static byte[] Png200x100()
    {
        using var bitmap = new SKBitmap(200, 100);
        bitmap.Erase(SKColors.Gray);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static MloopObjectDetector Build(FakeHandler handler, string model = "hardhat") =>
        new(MloopClient.Create("http://mloop.test", 30, "jwt-abc", handler),
            new DetectionOptions { Provider = "mloop", Endpoint = "http://mloop.test", Model = model, Token = "jwt-abc" });

    private static string Response(string detections) =>
        $$"""{"modelName":"hardhat","task":"object-detection","count":1,"predictions":[{"imageSha256":"ab12","detections":{{detections}},"predictionId":"p1"}],"warnings":[]}""";

    // ── mloop: 요청 · 응답 파싱 ───────────────────────────────────────────

    [Fact]
    public async Task Mloop_SendsBase64Content_ModelAndBearer()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Response("[]"));
        var png = Png200x100();

        await Build(handler).DetectAsync(png);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.StartsWith("http://mloop.test/predict", handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.Contains("hardhat", handler.LastRequest.RequestUri.Query);
        Assert.Equal("jwt-abc", handler.LastRequest.Headers.Authorization!.Parameter);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(Convert.ToBase64String(png), body.RootElement.GetProperty("images")[0].GetString());
    }

    [Fact]
    public async Task Mloop_ConvertsPixelCoordinates_ToImageRatios()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Response(
            """[{"label":"helmet","score":0.9,"x0":20,"y0":10,"x1":60,"y1":50}]"""));

        var set = await Build(handler).DetectAsync(Png200x100());

        var box = Assert.Single(set.Boxes);
        Assert.Equal("helmet", box.Class);
        Assert.Equal(0.1, box.X, precision: 6);   // 20 / 200
        Assert.Equal(0.1, box.Y, precision: 6);   // 10 / 100
        Assert.Equal(0.2, box.W, precision: 6);   // (60 - 20) / 200
        Assert.Equal(0.4, box.H, precision: 6);   // (50 - 10) / 100
        Assert.Equal(0.9, box.Confidence, precision: 6);
    }

    [Fact]
    public async Task Mloop_NoDetections_IsAnEmptyList_NotAFailure()
    {
        var set = await Build(new FakeHandler(HttpStatusCode.OK, Response("[]"))).DetectAsync(Png200x100());

        Assert.Empty(set.Boxes);
    }

    [Fact]
    public async Task Mloop_KeepsEveryBox_InServerOrder_AcrossClasses()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Response(
            """[{"label":"head","score":0.4,"x0":0,"y0":0,"x1":10,"y1":10},{"label":"helmet","score":0.8,"x0":100,"y0":0,"x1":120,"y1":20}]"""));

        var set = await Build(handler).DetectAsync(Png200x100());

        Assert.Equal(["head", "helmet"], set.Boxes.Select(b => b.Class));
    }

    [Fact]
    public async Task Mloop_ClampsSlightlyOutOfBoundsBox_AndScore()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Response(
            """[{"label":"helmet","score":1.7,"x0":-5,"y0":0,"x1":210,"y1":100}]"""));

        var box = Assert.Single((await Build(handler).DetectAsync(Png200x100())).Boxes);

        Assert.Equal(0.0, box.X);
        Assert.Equal(1.0, box.W);
        Assert.Equal(1.0, box.Confidence);
    }

    // ── mloop: 약속한 모양이 아니면 개수를 바꾸지 않고 실패한다 ──────────────

    [Theory]
    [InlineData("""[{"score":0.9,"x0":0,"y0":0,"x1":10,"y1":10}]""")]                          // label 없음
    [InlineData("""[{"label":"a","score":"high","x0":0,"y0":0,"x1":10,"y1":10}]""")]          // score 가 숫자가 아님
    [InlineData("""[{"label":"a","score":0.9,"x0":0,"y0":0,"x1":10}]""")]                     // 좌표 누락
    [InlineData("""[{"label":"a","score":0.9,"x0":10,"y0":10,"x1":10,"y1":50}]""")]           // 넓이 없음
    [InlineData("""[{"label":"a","score":0.9,"x0":50,"y0":0,"x1":10,"y1":50}]""")]            // 뒤집힌 상자
    [InlineData("""[{"label":"a","score":0.9,"x0":300,"y0":0,"x1":400,"y1":50}]""")]          // 이미지 밖
    [InlineData("""["not-a-box"]""")]
    public async Task Mloop_MalformedBox_Throws_InsteadOfQuietlyDroppingIt(string detections)
    {
        var detector = Build(new FakeHandler(HttpStatusCode.OK, Response(detections)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => detector.DetectAsync(Png200x100()));
    }

    [Fact]
    public async Task Mloop_ClassificationResponse_IsNotADetectionModel()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"predictions":[{"predictedLabel":"ng","probabilities":{"ng":0.9,"ok":0.1},"confidence":0.9}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Build(handler).DetectAsync(Png200x100()));

        Assert.Contains("detections", ex.Message);
    }

    [Fact]
    public async Task Mloop_ServerError_SurfacesTheServerMessage()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, """{"error":"model 'hardhat' not found"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Build(handler).DetectAsync(Png200x100()));

        Assert.Contains("hardhat", ex.Message);
    }

    [Fact]
    public async Task Mloop_UnreadableImage_Throws_BeforeCallingTheServer()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Response("[]"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Build(handler).DetectAsync(new byte[] { 1, 2, 3 }));

        Assert.Null(handler.LastRequest);
    }

    // ── none · mock · Factory ───────────────────────────────────────────

    [Fact]
    public async Task Disabled_IsOff_AndRefusesToDetect()
    {
        var detector = ObjectDetectorFactory.Create(new DetectionOptions());

        Assert.Equal("none", detector.Name);
        Assert.False(detector.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => detector.DetectAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task Mock_IsDeterministic_ForTheSameBytes_AndStaysInsideTheImage()
    {
        var detector = ObjectDetectorFactory.Create(new DetectionOptions { Provider = "mock" });
        var bytes = Enumerable.Range(0, 40).Select(i => (byte)(i * 7)).ToArray();

        var first = await detector.DetectAsync(bytes);
        var second = await detector.DetectAsync(bytes);

        Assert.Equal(first.Boxes, second.Boxes);
        Assert.All(first.Boxes, b =>
        {
            Assert.InRange(b.X, 0, 1);
            Assert.InRange(b.X + b.W, 0, 1);
            Assert.InRange(b.Y + b.H, 0, 1);
            Assert.InRange(b.Confidence, 0, 1);
        });
    }

    [Theory]
    [InlineData("mloop", "", "m")]   // Endpoint 없음
    [InlineData("mloop", "http://m", "")] // Model 없음
    [InlineData("grpc", "", "")]
    public void Factory_RejectsBadConfiguration_AtStartup(string provider, string endpoint, string model) =>
        Assert.Throws<ArgumentException>(() => ObjectDetectorFactory.Create(
            new DetectionOptions { Provider = provider, Endpoint = endpoint, Model = model }));

    [Fact]
    public void Factory_RejectsOutOfRangeTimeout() =>
        Assert.Throws<ArgumentException>(() => ObjectDetectorFactory.Create(
            new DetectionOptions { Provider = "mloop", Endpoint = "http://m", TimeoutSeconds = 0 }));

    [Fact]
    public void DefaultOptions_NeedNoKeys() =>
        Assert.False(ObjectDetectorFactory.Create(new DetectionOptions()).IsEnabled);
}
