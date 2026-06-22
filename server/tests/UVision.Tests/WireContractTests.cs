using System.Text.Json;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

public class WireContractTests
{
    // ambient 정책 없는 기본 옵션(임베드 호스트가 camelCase일 때를 모사).
    private static readonly JsonSerializerOptions Plain = new();

    [Fact]
    public void InspectResponse_Serializes_SnakeCase_Without_Ambient_Policy()
    {
        var json = JsonSerializer.Serialize(new InspectResponse
        {
            Verdict = Verdict.NG, Findings = "f", Confidence = 0.5,
            Timestamp = "t", ImageId = "img_1",
        }, Plain);

        Assert.Contains("\"image_id\":\"img_1\"", json);
        Assert.Contains("\"verdict\":\"NG\"", json); // 모델 부착 enum converter 유지
    }

    [Fact]
    public void ScenarioInput_Deserializes_SnakeCase_Without_Ambient_Policy()
    {
        var input = JsonSerializer.Deserialize<ScenarioInput>(
            "{\"name\":\"n\",\"motion_threshold\":9,\"ng_labels\":{\"r1\":\"scratch\"}}", Plain)!;

        Assert.Equal("n", input.Name);
        Assert.Equal(9, input.MotionThreshold);
        Assert.Equal("scratch", input.NgLabels["r1"]);
    }

    [Fact]
    public void StoredResult_RoundTrips_SnakeCase()
    {
        var json = JsonSerializer.Serialize(new StoredResult
        {
            ScenarioId = "s", ImageId = "i", Verdict = Verdict.OK, Findings = "",
            Confidence = 1.0, Timestamp = "t", ImageFile = "i.jpg",
        }, Plain);
        Assert.Contains("\"scenario_id\":\"s\"", json);
        Assert.Contains("\"image_file\":\"i.jpg\"", json);
        Assert.Contains("\"device_label\":\"\"", json);

        var back = JsonSerializer.Deserialize<StoredResult>(json, Plain)!;
        Assert.Equal("s", back.ScenarioId);
        Assert.Equal("i.jpg", back.ImageFile);
        Assert.Equal("", back.DeviceLabel);   // optional 기본값
    }

    [Fact]
    public void ReferenceInfo_Label_Serializes_Numeric()
    {
        var json = JsonSerializer.Serialize(
            new ReferenceInfo { RefId = "r1", Label = ReferenceLabel.Ng, NgLabel = "scratch" }, Plain);
        Assert.Contains("\"ref_id\":\"r1\"", json);
        Assert.Contains("\"label\":1", json);   // numeric (0/1) — value 직렬화 불변 보존
        Assert.Contains("\"ng_label\":\"scratch\"", json);
    }

    [Fact]
    public void DetectionUnavailableResponse_Serializes_SnakeCase_WithHint()
    {
        var json = JsonSerializer.Serialize(new DetectionUnavailableResponse
        {
            Reason = "vlm_unavailable",
            MlHint = new MlResult { Label = "ng", Confidence = 0.8 },
        }, Plain);

        Assert.Contains("\"detection_unavailable\":true", json);
        Assert.Contains("\"reason\":\"vlm_unavailable\"", json);
        Assert.Contains("\"ml_hint\":", json);
        Assert.Contains("\"label\":\"ng\"", json);
    }

    [Fact]
    public void DetectionUnavailableResponse_OmitsHint_WhenNull()
    {
        var json = JsonSerializer.Serialize(new DetectionUnavailableResponse
        {
            Reason = "vlm_unavailable",
        }, Plain);

        Assert.Contains("\"detection_unavailable\":true", json);
        Assert.DoesNotContain("ml_hint", json);
    }
}
