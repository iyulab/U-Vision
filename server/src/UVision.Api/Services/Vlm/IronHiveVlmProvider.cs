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
                new UserMessage
                {
                    Content =
                    [
                        new TextMessageContent { Value = "이 제품 이미지를 판정하라." },
                        // 원본(Python) 동등: content_type 무관하게 jpeg 로 전송.
                        new ImageMessageContent
                        {
                            Format = ImageFormat.Jpeg,
                            Base64 = Convert.ToBase64String(image.Span),
                        },
                    ],
                },
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
}
