using System.Security.Cryptography;
using System.Text;

namespace UVision.Api.Auth;

/// <summary>
/// 관리자 PIN 설정 — <c>ADMIN_PIN</c> 환경변수.
///
/// 단일조직 셀프호스트 전제: 계정·세션·역할 시스템 없이, 관리자 셋업 엔드포인트만 단일 PIN 으로 보호한다.
/// 운영 화면(<c>/api/inspect</c>·<c>/api/health</c>)은 무인증.
/// </summary>
public sealed class AdminPinOptions
{
    /// <summary>미설정(null/빈값) 시 관리 기능은 비활성(요청 503).</summary>
    public string? Pin { get; set; }

    public bool IsConfigured => !string.IsNullOrEmpty(Pin);
}

/// <summary>
/// 관리자 엔드포인트 보호 필터. 요청 헤더 <c>X-Admin-Pin</c> 을 설정 PIN 과 constant-time 비교한다.
/// </summary>
public sealed class AdminPinFilter : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Pin";

    private readonly AdminPinOptions _options;

    public AdminPinFilter(AdminPinOptions options) => _options = options;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!_options.IsConfigured)
            return Results.Problem(statusCode: 503, detail: "관리자 PIN 미설정(ADMIN_PIN)");

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!FixedTimeEquals(provided, _options.Pin!))
            return Results.Problem(statusCode: 401, detail: "관리자 PIN 불일치");

        return await next(context);
    }

    /// <summary>타이밍 공격 방지 비교. 길이가 달라도 일정 시간(FixedTimeEquals 내부 처리).</summary>
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>관리자 PIN 필터를 엔드포인트에 부착하는 확장.</summary>
public static class AdminPinExtensions
{
    public static TBuilder RequireAdminPin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, AdminPinFilter>();
}
