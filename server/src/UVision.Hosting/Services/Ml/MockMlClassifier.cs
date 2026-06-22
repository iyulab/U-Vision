using System.Security.Cryptography;

namespace UVision.Api.Services.Ml;

/// <summary>
/// Mock ML 분류기 — 키/모델 없이 plumbing 을 구동하는 결정론적 구현(테스트·데모).
///
/// ⚠️ 정직성: 이미지 내용을 보지 않는다. 바이트 해시로 ok/ng 를 결정론적으로 가른다.
/// 실제 분류 검증은 MloopClassifier(C41) + 학습 모델 + 실데이터 스파이크(FW-3)에서만 가능하다.
///
/// <para>
/// 분기 바이트는 <see cref="MockVlmProvider"/>(digest[0]) 와 <b>독립</b>(digest[2])이다 — 동일
/// 이미지에서도 두 mock 이 가끔 불일치하도록(실 VLM·ML 은 독립 모델). ③/④ 의 핵심 plumbing
/// (불일치 → <c>requires_review</c> → 검토 큐)을 mock provider 만으로도 구동·데모하기 위함.
/// </para>
/// </summary>
public sealed class MockMlClassifier : IMlClassifier
{
    public string Name => "mock";

    public bool IsEnabled => true;

    public Task<MlClassification> ClassifyAsync(
        ReadOnlyMemory<byte> image, string scenarioId, CancellationToken cancellationToken = default)
    {
        var digest = SHA256.HashData(image.Span);
        var isNg = digest[2] % 3 == 0;                       // VLM(digest[0]) 과 독립 분기 → 가끔 불일치
        var ngScore = Math.Round(0.50 + (digest[3] / 255.0) * 0.49, 4); // 0.50~0.99
        var okScore = Math.Round(1.0 - ngScore, 4);
        var label = isNg ? "ng" : "ok";
        return Task.FromResult(new MlClassification
        {
            Label = label,
            Confidence = isNg ? ngScore : okScore,
            Scores = new Dictionary<string, double> { ["ok"] = okScore, ["ng"] = ngScore },
        });
    }
}
