using System.Collections;
using HoneAI;
using SkiaSharp;
using UVision.Api.Configuration;

namespace UVision.Api.Services.Counting;

/// <summary>
/// MLoop <c>mloop serve</c> 의 <c>POST /predict</c>(object-detection 모델)를 쓰는 검출기 — 프로세스 경계로만 소비한다
/// (전용 ML 분류기 · 임베더와 같은 꼴, HTTP 계약은 HoneAI <see cref="IMLoopClient"/>(<c>PredictImagesAsync</c>)가 소유).
/// 이미지 <b>내용</b>을 base64 로 보내므로 MLoop 이 같은 호스트에 있을 필요가 없다.
/// <para>
/// 관측(실 <c>mloop serve</c> 0.49.0, 안전모 대리 데이터로 학습한 검출 모델): 상자 한 개는
/// <c>{label, score, x0, y0, x1, y1}</c>(좌표는 이미지 픽셀), 사진마다 <c>{imageSha256, detections[], predictionId}</c>, 바깥에
/// <c>modelName</c> · <c>modelSha256</c> · <c>task</c> · <c>predictions[]</c> · <c>warnings[]</c> — 이 클래스로 엔진을 실 서버에 붙여 12장을 셌을 때 직접 호출한 상자 수와 모두 같았다.
/// ⚠️ 한계: 검출 서버는 점수 0.5 미만 상자를 보내지 않고(0.49.0 은 호출자가 낮출 수 있다) 지금 사용하는 클라이언트 요청에는 그 문턱을
/// 실을 자리가 없어, 신뢰도 문턱이 0.5 미만이면 효력이 없다. TODO(upstream): 요청에 점수 문턱이 생기면 문턱 − 흔들림 폭(하한 0.05)을 보낸다.
/// </para>
/// 응답이 약속한 모양이 아니면(상자 필드 누락 · 비유한 숫자 · 넓이 없는 상자) 조용히 걸러 개수를 바꾸지 않고 예외로 알린다 —
/// 호출 측은 개수를 모르는 것으로 다룬다.
/// </summary>
public sealed class MloopObjectDetector(IMLoopClient client, DetectionOptions options) : IObjectDetector
{
    public string Name => "mloop";

    public bool IsEnabled => true;

    public async Task<DetectionSet> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        var (width, height) = SizeOf(image);

        IReadOnlyList<ITracedPrediction<MLoopPredictionResult>> predictions;
        try
        {
            predictions = await client.PredictImagesAsync(new MLoopImagePredictionRequest([image], options.Model), cancellationToken);
        }
        catch (MLoopClientException ex)
        {
            // 400(못 읽는 이미지 · 검출 모델이 아님) · 404(모델 없음) · 413 — 서버 본문이 메시지에 있다.
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (predictions.Count != 1)
            throw new InvalidOperationException($"mloop /predict 가 이미지 1장에 예측 {predictions.Count}개를 냈다");
        var prediction = predictions[0].Value;
        if (!prediction.Outputs.TryGetValue("detections", out var raw) || raw is not IEnumerable list || raw is string)
            throw new InvalidOperationException("mloop /predict 응답에 detections 목록이 없습니다(검출 모델이 아닌가?)");

        var boxes = new List<Detection>();
        foreach (var item in list)
            boxes.Add(BoxOf(item, width, height));
        return new DetectionSet(boxes, prediction.Model?.Sha256);
    }

    private static Detection BoxOf(object? item, int width, int height)
    {
        if (item is not IReadOnlyDictionary<string, object?> box)
            throw new InvalidOperationException("mloop detections 항목이 객체가 아니다");
        if (box.GetValueOrDefault("label") is not string label || label.Length == 0)
            throw new InvalidOperationException("mloop 상자에 label 이 없다");

        var score = Number(box, "score");
        double x0 = Number(box, "x0"), y0 = Number(box, "y0"), x1 = Number(box, "x1"), y1 = Number(box, "y1");
        // 경계 밖으로 조금 나간 상자는 이미지 안으로 자른다 — 그 뒤에도 넓이가 없으면 약속한 상자가 아니다.
        double left = Math.Clamp(x0 / width, 0, 1), top = Math.Clamp(y0 / height, 0, 1);
        double right = Math.Clamp(x1 / width, 0, 1), bottom = Math.Clamp(y1 / height, 0, 1);
        if (right <= left || bottom <= top)
            throw new InvalidOperationException($"mloop 상자의 넓이가 없다 ({x0}, {y0}, {x1}, {y1})");

        // 신뢰도는 신뢰 경계에서 [0,1] 로 자른다(분류기의 confidence 와 같은 규율).
        return new Detection(label, left, top, right - left, bottom - top, Math.Clamp(score, 0, 1));
    }

    private static double Number(IReadOnlyDictionary<string, object?> box, string key)
    {
        if (box.GetValueOrDefault(key) is not (long or double or decimal or int or float))
            throw new InvalidOperationException($"mloop 상자의 {key} 가 숫자가 아니다");
        var value = Convert.ToDouble(box[key], System.Globalization.CultureInfo.InvariantCulture);
        return double.IsFinite(value) ? value : throw new InvalidOperationException($"mloop 상자의 {key} 가 유한하지 않다");
    }

    // 좌표는 보낸 이미지의 픽셀이라 비율로 옮기려면 크기가 필요하다. 디코딩하지 않고 머리말만 읽는다.
    private static (int Width, int Height) SizeOf(ReadOnlyMemory<byte> image)
    {
        using var data = SKData.CreateCopy(image.Span);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("읽을 수 없는 이미지 데이터");
        return (codec.Info.Width, codec.Info.Height);
    }
}
