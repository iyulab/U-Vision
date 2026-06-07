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

    // 출력은 프롬프트로 강제한다 — provider 의 구조화 출력(json_schema)에 의존하지 않는다.
    // 근거: OpenAI 호환 셀프호스트(llama.cpp/GPUStack)는 ironhive 가 생성하는 json_schema 를
    // grammar 로 강제하지 못해(union type+pattern, type 없는 enum) 산문을 방출 → 서버 500.
    // 명시적 JSON 지시는 cloud(structured output 이전 표준)·셀프호스트 모두에서 안정적이다.
    // 상세: claudedocs/issues/ISSUE-ironhive-*-jsonschema-llamacpp.md
    private const string OutputContract =
        "\n\n[출력 형식]\n" +
        "반드시 아래 JSON 객체 **하나만** 출력한다. 마크다운 코드펜스(```)나 그 외 설명·머리말을 절대 덧붙이지 않는다.\n" +
        "{\"verdict\": \"OK\" 또는 \"NG\", \"findings\": \"불량 소견(OK 면 빈 문자열)\", \"confidence\": 0.0~1.0 사이 숫자}";

    public static string BuildSystemPrompt(ScenarioContext scenario)
    {
        var criteria = string.IsNullOrEmpty(scenario.Criteria)
            ? "(기준 미지정 — 일반적 외관 결함 기준 적용)"
            : scenario.Criteria;
        return $"{System}\n\n[판정 기준]\n{criteria}{OutputContract}";
    }
}
