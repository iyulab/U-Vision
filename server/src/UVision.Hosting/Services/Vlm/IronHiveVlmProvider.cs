using System.Text.Json;
using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;
using IronHive.Abstractions.Messages.Roles;
using UVision.Api.Models;

namespace UVision.Api.Services.Vlm;

/// <summary>
/// ironhive(IMessageService) 기반 VLM 어댑터 — openai/google/gpustack 모두 처리한다.
/// ironhive 가 provider 추상화·비전 입력을 흡수하므로 provider 별 구현을 분리하지 않는다.
///
/// 출력 계약은 ironhive 구조화 출력(<c>Output = typeof(InspectionResult)</c>)으로 강제한다 —
/// JsonSchemaFactory 가 발행하는 json_schema 가 grammar 로 강제되어 모델이 스키마에 맞는 JSON 을 낸다.
/// ironhive 0.7.4 가 셀프호스트 비호환(수치 union+pattern·type 없는 enum)을 정규화하기 전까지는
/// 프롬프트로 JSON 을 강제하는 app-level 우회를 썼으나, 0.7.4 에서 GPUStack(llama.cpp) 라이브 검증 완료
/// → 구조화 출력으로 복귀(우회 제거). 응답 파싱은 <see cref="ExtractJson"/> 으로 reasoning 모델의
/// 머리말/펜스를 관용 처리한다(grammar 가 content 채널을 강제하나 방어적으로 유지).
/// 상세: claudedocs/issues/closed/ISSUE-ironhive-*-jsonschema-llamacpp.md
/// (원본: server/app/services/vlm/{openai,google}_provider.py 를 단일 어댑터로 통합)
/// </summary>
public sealed class IronHiveVlmProvider : IVlmProvider
{
    // 모델 출력 JSON 의 속성명 대소문자/순서에 관대하게. Verdict enum 의 "OK"/"NG" 변환은
    // 모델에 부착된 JsonStringEnumConverter<Verdict> 가 처리.
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // reasoning 모델(예: GPUStack 의 qwen3 계열)은 답(JSON) 앞에 사고 토큰을 소비한다. 한도가 낮으면
    // 사고가 예산을 다 써 content 가 빈 채로 truncate(finish_reason=length)되어 역직렬화가 실패한다.
    // 사고 + 구조화 JSON 이 함께 들어가도록 넉넉히 둔다. (비추론 provider 엔 영향 없음 — 짧게 끝남.)
    private const int MaxOutputTokens = 4096;

    private readonly IMessageService _messageService;
    private readonly string _model;

    public IronHiveVlmProvider(IMessageService messageService, string providerName, string model)
    {
        _messageService = messageService;
        Name = providerName;
        _model = model;
    }

    public string Name { get; }

    public async Task<InspectionResult> InspectAsync(
        ReadOnlyMemory<byte> image,
        ScenarioContext scenario,
        CancellationToken cancellationToken = default)
    {
        var request = new MessageRequest
        {
            Provider = Name,
            Model = _model,
            System = VlmPrompt.BuildSystemPrompt(scenario),
            Messages =
            [
                new UserMessage { Content = BuildContent(image, scenario.References) },
            ],
            Output = typeof(InspectionResult), // 구조화 출력(json_schema grammar 강제) — ironhive 0.7.4+
            MaxTokens = MaxOutputTokens,       // reasoning 모델 truncation 방지(위 주석)
        };

        var response = await _messageService.GenerateMessageAsync(request, cancellationToken);

        var json = response.Message.Content
            .OfType<TextMessageContent>()
            .Select(c => c.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"VLM provider '{Name}' 가 텍스트 응답을 반환하지 않음.");
        }

        var result = JsonSerializer.Deserialize<InspectionResult>(ExtractJson(json), DeserializeOptions)
            ?? throw new InvalidOperationException($"VLM 응답 역직렬화 실패: {json}");

        return result with { Confidence = NormalizeConfidence(result.Confidence) };
    }

    /// <summary>
    /// 모델 응답 텍스트에서 JSON 객체를 관용적으로 추출한다(첫 '{' ~ 마지막 '}'). 구조화 출력의 grammar 가
    /// content 채널을 강제하므로 보통 순수 JSON 이 오지만, reasoning 모델/백엔드가 펜스·머리말을 덧붙일
    /// 가능성에 대한 defense-in-depth 다(우회 의존이 아니라 견고한 역직렬화).
    /// </summary>
    internal static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text.Trim();
    }

    /// <summary>
    /// confidence 의 도메인 불변식(0.0~1.0)을 경계에서 강제한다. 구조화 출력 json_schema 는 타입(number)만
    /// 강제하고 값 범위는 강제하지 못해, 모델이 백분율(예: 95.0)로 방출할 수 있다(프롬프트로 1차 억제하나
    /// non-deterministic). 1 초과·100 이하는 백분율로 보고 /100, 그 외는 [0,1] 로 clamp 한다.
    /// </summary>
    internal static double NormalizeConfidence(double value)
    {
        if (value > 1.0 && value <= 100.0)
        {
            value /= 100.0;
        }
        return Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// 판정 메시지 본문을 만든다. few-shot 기준 이미지가 있으면 (라벨 텍스트 + 이미지) 쌍으로
    /// 먼저 제시하고, 마지막에 판정 대상 이미지를 둔다.
    ///
    /// ⚠️ UNVERIFIED 판정효과: 기준 이미지를 컨텍스트로 주는 결선(base64/Format/순서)은 검증되나,
    /// few-shot 이 실제로 판정을 개선하는지는 M0.1(실 provider + 대표 이미지)에서만 확인된다.
    /// </summary>
    internal static List<MessageContent> BuildContent(
        ReadOnlyMemory<byte> image, IReadOnlyList<ReferenceImage> references)
    {
        var content = new List<MessageContent>();

        if (references.Count > 0)
        {
            content.Add(new TextMessageContent
            {
                Value = "먼저 기준 이미지다. 이를 참고해 판정 대상을 평가하라.",
            });
            foreach (var reference in references)
            {
                var label = reference.Label == ReferenceLabel.Ok
                    ? "[OK 기준]"
                    : $"[NG 기준{(string.IsNullOrEmpty(reference.NgLabel) ? "" : $" — {reference.NgLabel}")}]";
                content.Add(new TextMessageContent { Value = label });
                content.Add(new ImageMessageContent
                {
                    Format = reference.IsPng ? ImageFormat.Png : ImageFormat.Jpeg,
                    Base64 = Convert.ToBase64String(reference.Data.Span),
                });
            }
        }

        content.Add(new TextMessageContent { Value = "이 제품 이미지를 판정하라." });
        content.Add(new ImageMessageContent
        {
            Format = ImageFormat.Jpeg,
            Base64 = Convert.ToBase64String(image.Span),
        });

        return content;
    }
}
