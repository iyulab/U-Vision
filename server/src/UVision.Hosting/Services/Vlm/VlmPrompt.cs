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
        var prompt = $"{System}\n\n[판정 기준]\n{criteria}";
        // 불량 유형 — 시나리오가 선언했을 때만(이분 판정의 프롬프트는 그대로). 유형 id 는 경계 화이트리스트를 지난 값이다.
        if (scenario.DefectClasses is { Count: > 0 } defects)
            prompt += "\n\n[불량 유형]\n" +
                      $"NG 이면 defect_class 를 다음 중 하나로 답한다: {string.Join(", ", defects)}. " +
                      "OK 이면 defect_class 는 null 이다. 어느 유형인지 확신할 수 없으면 가장 가까운 유형을 고르고 confidence 를 낮춘다.";
        // 오투입 확인 — 기대 품번이 있을 때만 덧붙인다(검사 시나리오의 프롬프트는 그대로).
        // 확인할 수 없으면 NG: 불일치는 놓치면 안 되는 값이라 모호하면 사람에게 올린다.
        return scenario.ExpectedPart is { } expected
            ? $"{prompt}\n\n[오투입 확인]\n기대 품번: {expected}\n" +
              "이미지의 부품이 기대 품번과 같으면 OK, 다르거나 확인할 수 없으면 NG 로 판정한다."
            : prompt;
    }
}
