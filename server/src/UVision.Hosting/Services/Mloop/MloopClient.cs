using System.Net.Http.Headers;
using HoneAI;

namespace UVision.Api.Services.Mloop;

/// <summary>
/// ML 분류기 · 기억 단이 함께 쓰는 <c>mloop serve</c> 클라이언트 구성 — 각자 설정(끝점 · 시간 제한 · 토큰)으로 하나씩 짓는다.
/// <paramref name="handler"/> 는 전송을 바꿔 끼울 때(계약 테스트의 fake handler)만 준다.
/// </summary>
public static class MloopClient
{
    public static IMLoopClient Create(
        string endpoint, int timeoutSeconds, string? token, HttpMessageHandler? handler = null)
    {
        // 상대 경로(predict?name= · embed?encoder=) 해석을 위해 base URL 에 trailing slash 보장.
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new HttpMLoopClient(http);
    }
}
