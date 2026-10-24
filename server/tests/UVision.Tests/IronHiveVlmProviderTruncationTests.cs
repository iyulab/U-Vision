using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 생각하는 모델이 출력 토큰 한도를 추론에 다 쓰고 끝난 응답 — «응답 없음 · 역직렬화 실패»로 가리지 않고 한도를 밝혀 던진다.
/// </summary>
public class IronHiveVlmProviderTruncationTests
{
    private sealed class FixedMessageService(MessageDoneReason reason, string? text) : IMessageService
    {
        public Task<MessageResponse> GenerateMessageAsync(MessageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MessageResponse
            {
                DoneReason = reason,
                Message = Message.Assistant(text is null ? [] : [new TextMessageContent { Value = text }]),
            });

        public IReadOnlyDictionary<string, IMessageGenerator> Generators => throw new NotSupportedException();

        public IAsyncEnumerable<StreamingMessageResponse> GenerateStreamingMessageAsync(
            MessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CountTokensAsync(MessageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static readonly ScenarioContext Scenario = new() { ScenarioId = "s", Criteria = "c", References = [] };

    [Theory]
    [InlineData(null)]                           // 추론만 하고 답 없음
    [InlineData("{\"verdict\": \"NG\", \"fin")]  // 답 중간에서 끊김
    public async Task CutAtTheTokenLimit_SaysSo(string? text)
    {
        var provider = new IronHiveVlmProvider(new FixedMessageService(MessageDoneReason.MaxTokens, text), "openai-compatible", "m", 4096);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InspectAsync(new byte[] { 0xFF, 0xD8 }, Scenario));

        Assert.Contains("출력 토큰 한도(4096)", ex.Message);
        Assert.Contains("MaxOutputTokens", ex.Message);
    }

    [Fact]
    public async Task EmptyAnswerForAnotherReason_IsNotBlamedOnTheLimit()
    {
        var provider = new IronHiveVlmProvider(new FixedMessageService(MessageDoneReason.EndTurn, null), "openai-compatible", "m", 4096);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InspectAsync(new byte[] { 0xFF, 0xD8 }, Scenario));

        Assert.DoesNotContain("출력 토큰 한도", ex.Message);
    }
}
