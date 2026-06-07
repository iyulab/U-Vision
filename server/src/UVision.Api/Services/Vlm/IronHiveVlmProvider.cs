using System.Text.Json;
using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;
using IronHive.Abstractions.Messages.Roles;
using UVision.Api.Models;

namespace UVision.Api.Services.Vlm;

/// <summary>
/// ironhive(IMessageService) 기반 VLM 어댑터 — openai/google 둘 다 처리한다.
/// ironhive 가 provider 추상화·비전 입력·구조화 출력을 흡수하므로 provider 별 구현을 분리하지 않는다.
///
/// ⚠️ UNVERIFIED — API 키 부재로 실호출 미검증. 구조화 출력(Output=typeof) 의 실제 응답 형태·
/// latency 는 M0.1(벤치마크 하네스, 키 필요)에서 검증해야 한다. 그 전까지 옳다고 가정하지 말 것.
/// (원본: server/app/services/vlm/{openai,google}_provider.py 를 단일 어댑터로 통합)
/// </summary>
public sealed class IronHiveVlmProvider : IVlmProvider
{
    // ironhive JsonSchemaFactory 가 PropertyNameCaseInsensitive 로 스키마를 만들므로 역직렬화도 동일하게.
    // Verdict enum 의 "OK"/"NG" 변환은 모델에 부착된 JsonStringEnumConverter<Verdict> 가 처리.
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
            Output = typeof(InspectionResult), // 구조화 출력 강제
        };

        var response = await _messageService.GenerateMessageAsync(request, cancellationToken);

        var json = response.Message.Content
            .OfType<TextMessageContent>()
            .Select(c => c.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"VLM provider '{Name}' 가 구조화 출력 텍스트를 반환하지 않음.");
        }

        return JsonSerializer.Deserialize<InspectionResult>(json, DeserializeOptions)
            ?? throw new InvalidOperationException($"VLM 응답 역직렬화 실패: {json}");
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
