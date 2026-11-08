using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UVision.Edge.Worker;

/// <summary>The model server could not give a usable answer for a frame (unreachable, error status, unreadable body).</summary>
public sealed class DetectorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Finds objects in one frame. Boxes come back normalised to 0..1 so nothing downstream depends on the image size.</summary>
public interface IDetector
{
    Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct);
}

/// <summary>
/// Asks a <c>mloop serve</c> object-detection model for boxes over its REST boundary (<c>POST /predict</c>, the image as base64). The score
/// floor goes in the request body: the server's own default (0.5) would hide low-confidence boxes the caller may want to see.
/// </summary>
public sealed class HttpDetector(HttpClient http, double scoreThreshold, IReadOnlySet<string>? classes) : IDetector
{
    public async Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { images = new[] { Convert.ToBase64String(frame.Jpeg) }, scoreThreshold });
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "predict") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new DetectorException($"the model server answered {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Parse(doc.RootElement, frame.Width, frame.Height, scoreThreshold, classes);
        }
        catch (HttpRequestException e) { throw new DetectorException("the model server is unreachable", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new DetectorException("the model server did not answer in time", e); }
        catch (JsonException e) { throw new DetectorException("the model server's answer is not readable", e); }
    }

    /// <summary>Reads <c>predictions[0].detections[]</c> (<c>label</c>, <c>score</c>, pixel <c>x0 y0 x1 y1</c>) into normalised boxes.</summary>
    internal static IReadOnlyList<Detection> Parse(JsonElement root, int width, int height, double floor, IReadOnlySet<string>? classes)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("predictions", out var predictions)
            || predictions.ValueKind != JsonValueKind.Array || predictions.GetArrayLength() < 1
            || !predictions[0].TryGetProperty("detections", out var detections) || detections.ValueKind != JsonValueKind.Array)
            throw new DetectorException("the model server's answer has no predictions[0].detections");

        var list = new List<Detection>();
        foreach (var d in detections.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object) continue;
            if (!d.TryGetProperty("label", out var l) || l.ValueKind != JsonValueKind.String || l.GetString() is not { Length: > 0 } label) continue;
            if (classes is { Count: > 0 } && !classes.Contains(label)) continue;
            if (!Num(d, "score", out var score) || score < floor) continue;
            if (!Num(d, "x0", out var x0) || !Num(d, "y0", out var y0) || !Num(d, "x1", out var x1) || !Num(d, "y1", out var y1)) continue;

            var nx0 = Math.Clamp(Math.Min(x0, x1) / width, 0, 1);
            var ny0 = Math.Clamp(Math.Min(y0, y1) / height, 0, 1);
            var nx1 = Math.Clamp(Math.Max(x0, x1) / width, 0, 1);
            var ny1 = Math.Clamp(Math.Max(y0, y1) / height, 0, 1);
            if (nx1 - nx0 <= 0 || ny1 - ny0 <= 0) continue;
            list.Add(new Detection(label, score, new Box(nx0, ny0, nx1 - nx0, ny1 - ny0)));
        }
        return list;
    }

    private static bool Num(JsonElement o, string name, out double value)
    {
        value = 0;
        return o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value) && double.IsFinite(value);
    }

    public static HttpClient CreateClient(Uri baseUrl, string? bearer)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(bearer)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }
}
