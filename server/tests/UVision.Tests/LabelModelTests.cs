using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public class LabelModelTests
{
    [Fact]
    public void Normalized_LegacySidecar_SynthesizesSingleLabelEvent()
    {
        // 구 사이드카: history 없음.
        var legacy = new StoredLabel { ImageId = "i", Label = "NG", Timestamp = "2026-06-09T00:00:00Z" };
        var n = legacy.Normalized();

        Assert.NotNull(n.History);
        Assert.Single(n.History!);
        Assert.Equal("NG", n.History![0].Label);
        Assert.Equal(LabelMode.Label, n.History[0].Mode);
        Assert.Equal("2026-06-09T00:00:00Z", n.History[0].At);
        Assert.Equal(LabelAuditStatus.Unaudited, n.Audit!.Status);
    }

    [Fact]
    public void Normalized_AlreadyHasHistory_IsUnchanged()
    {
        var existing = new StoredLabel
        {
            ImageId = "i", Label = "NG", Timestamp = "t",
            History = [new LabelEvent { Label = "NG", By = "dev", At = "t", Mode = LabelMode.Label }],
            Audit = new LabelAudit { Status = LabelAuditStatus.Consistent },
        };
        Assert.Same(existing.History, existing.Normalized().History);
    }

    [Fact]
    public void StoredLabel_Serializes_LegacyFieldsByteIdentical_WhenNoHistory()
    {
        // 하위호환: history/audit 없는 객체는 기존 {image_id,label,timestamp} 만 직렬화.
        var json = JsonSerializer.Serialize(
            new StoredLabel { ImageId = "i", Label = "NG", Timestamp = "t" }, StoragePaths.Json);
        Assert.Contains("\"label\": \"NG\"", json);
        Assert.DoesNotContain("history", json);
        Assert.DoesNotContain("audit", json);
    }

    [Fact]
    public void StoredLabel_RoundTrips_HistoryAndAudit()
    {
        var src = new StoredLabel
        {
            ImageId = "i", Label = "NG", Timestamp = "t",
            History = [new LabelEvent { Label = "NG", By = "dev", At = "t", Mode = LabelMode.Audit }],
            Audit = new LabelAudit { Status = LabelAuditStatus.Conflicted, At = "t" },
        };
        var json = JsonSerializer.Serialize(src, StoragePaths.Json);
        var back = JsonSerializer.Deserialize<StoredLabel>(json, StoragePaths.Json)!;

        Assert.Equal(LabelMode.Audit, back.History![0].Mode);
        Assert.Equal(LabelAuditStatus.Conflicted, back.Audit!.Status);
    }
}
