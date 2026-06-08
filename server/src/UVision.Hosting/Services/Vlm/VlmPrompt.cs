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
        "NG 이면 불량 소견(findings)을 한국어로 구체적으로 기술한다. " +
        "confidence 는 0.0~1.0 사이 소수다(백분율·100 척도 금지 — 예: 95% 는 0.95). 경계 사례일수록 낮춘다.";

    // 출력 *형식*(verdict/findings/confidence JSON 의 필드명·타입·enum)은 ironhive 구조화 출력
    // (Output=typeof)의 json_schema 가 grammar 로 강제한다 — 프롬프트로 JSON shape 를 지시하지 않는다
    // (ironhive 0.7.4 에서 셀프호스트 호환 정규화 확인). 단 스키마는 타입만 강제하고 *값 범위/스케일*은
    // 강제하지 못하므로, confidence 의 0~1 스케일 같은 **필드 의미론**은 위 System 프롬프트가 소유한다.
    public static string BuildSystemPrompt(ScenarioContext scenario)
    {
        var criteria = string.IsNullOrEmpty(scenario.Criteria)
            ? "(기준 미지정 — 일반적 외관 결함 기준 적용)"
            : scenario.Criteria;
        return $"{System}\n\n[판정 기준]\n{criteria}";
    }
}
