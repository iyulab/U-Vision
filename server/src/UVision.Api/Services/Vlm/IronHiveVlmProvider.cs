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
/// 출력 계약은 <see cref="VlmPrompt"/> 의 프롬프트로 강제한다(JSON 한 객체). ironhive 의 구조화
/// 출력(Output=typeof)은 쓰지 않는다 — JsonSchemaFactory 가 생성하는 json_schema 를 OpenAI 호환
/// 셀프호스트(llama.cpp/GPUStack)가 grammar 로 강제하지 못해 산문 방출→서버 500 이 된다(M0.1 실측 확인).
/// 명시적 JSON 지시 + 관용 파싱이 cloud·셀프호스트 양쪽에서 안정적이다.
/// 상세: claudedocs/issues/ISSUE-ironhive-*-jsonschema-llamacpp.md
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

        return JsonSerializer.Deserialize<InspectionResult>(ExtractJson(json), DeserializeOptions)
            ?? throw new InvalidOperationException($"VLM 응답 역직렬화 실패: {json}");
    }

    /// <summary>
    /// 모델 응답 텍스트에서 JSON 객체를 관용적으로 추출한다. grammar 강제가 없으므로(프롬프트로만 지시)
    /// reasoning 모델이 코드펜스(```json)나 머리말을 덧붙일 수 있다 — 첫 '{' ~ 마지막 '}' 구간만 취한다.
    /// </summary>
    internal static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text.Trim();
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
