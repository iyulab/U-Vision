using UVision.Api.Models;

namespace UVision.Api.Services.Vlm;

/// <summary>
/// VLM 프롬프트 — provider 간 공유. 판정 일관성을 위해 한 곳에서 정의한다.
/// (원본: server/app/services/vlm/prompt.py)
/// </summary>
public static class VlmPrompt
{
    private const string System =
        "당신은 제조 현장의 비전 검사 판정기다. " +
        "주어진 제품 이미지를 시나리오의 판정 기준에 따라 OK 또는 NG 로 판정한다. " +
        "NG 이면 불량 소견을 한국어로 구체적으로 기술한다. " +
        "경계 사례일수록 confidence 를 낮춘다.";

    public static string BuildSystemPrompt(ScenarioContext scenario)
    {
        var criteria = string.IsNullOrEmpty(scenario.Criteria)
            ? "(기준 미지정 — 일반적 외관 결함 기준 적용)"
            : scenario.Criteria;
        return $"{System}\n\n[판정 기준]\n{criteria}";
    }
}
