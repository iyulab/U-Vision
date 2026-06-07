using System.Security.Cryptography;
using UVision.Api.Models;

namespace UVision.Api.Services.Vlm;

/// <summary>
/// Mock provider — 키 없이 plumbing 을 완전 구동하는 결정론적 구현.
///
/// ⚠️ 정직성: mock 판정은 <b>이미지 내용을 보지 않는다.</b> 바이트 해시로 OK/NG 를
/// 결정론적으로 가른다. mock 기반 E2E 통과는 "파이프라인이 흐른다"는 증거일 뿐,
/// "VLM 코어가 옳게 판정한다"는 증거가 아니다. 실제 판정 검증은 M0.1(실측, 키 필요)에서만 가능하다.
/// (원본: server/app/services/vlm/mock.py — 분기 로직 byte 단위로 동일)
/// </summary>
public sealed class MockVlmProvider : IVlmProvider
{
    private const string NgFindings =
        "모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).";

    public string Name => "mock";

    public Task<InspectionResult> InspectAsync(
        ReadOnlyMemory<byte> image,
        ScenarioContext scenario,
        CancellationToken cancellationToken = default)
    {
        var digest = SHA256.HashData(image.Span);
        // 결정론적 분기: 동일 이미지는 항상 동일 결과(테스트 재현성).
        var isNg = digest[0] % 3 == 0; // 약 1/3 확률로 NG
        // confidence: 해시 바이트를 0.70~0.99 로 사상.
        var confidence = Math.Round(0.70 + (digest[1] / 255.0) * 0.29, 2);
        return Task.FromResult(new InspectionResult
        {
            Verdict = isNg ? Verdict.NG : Verdict.OK,
            Findings = isNg ? NgFindings : "",
            Confidence = confidence,
        });
    }
}
