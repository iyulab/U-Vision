using UVision.Api.Models;

namespace UVision.Api.Auth;

/// <summary>
/// 관리자 PIN 설정 — <c>UVision:AdminPin</c>(appsettings) / <c>ADMIN_PIN</c>(env).
///
/// 단일조직 셀프호스트 전제: 계정·세션·역할 시스템 없이, 관리자 셋업 엔드포인트만 단일 PIN 으로 보호한다.
/// 운영 화면(<c>/api/inspect</c>·<c>/api/health</c>)은 무인증.
///
/// <para>이 옵션은 <b>명시적 config PIN</b> 만 담는다. 미설정(null/빈값) 시 <see cref="AdminPinService"/> 가
/// 파일시스템 부트스트랩(<c>{DataPath}/admin-pin.json</c>)으로 최초 PIN 설정을 허용한다.</para>
/// </summary>
public sealed class AdminPinOptions
{
    /// <summary>명시적 config PIN. 미설정 시 파일 기반 부트스트랩으로 넘어간다(<see cref="AdminPinService"/>).</summary>
    public string? Pin { get; set; }

    public bool IsConfigured => !string.IsNullOrEmpty(Pin);
}

/// <summary>
/// 관리자 엔드포인트 보호 필터. 요청 헤더 <c>X-Admin-Pin</c> 을 <see cref="AdminPinService"/> 로 검증한다
/// (config 평문 또는 파일 해시 constant-time 비교). 미설정 시 <c>setup_required</c> 마커를 실은 503.
/// </summary>
public sealed class AdminPinFilter : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Pin";

    private readonly AdminPinService _service;

    public AdminPinFilter(AdminPinService service) => _service = service;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;

        if (!await _service.IsConfiguredAsync(ct))
        {
            var status = await _service.GetStatusAsync(ct);
            return Results.Json(new AdminSetupRequiredResponse
            {
                SetupRequired = status.SetupRequired,
                Detail = "관리자 PIN 미설정 — UVision:AdminPin(appsettings) 또는 ADMIN_PIN(env) 을 설정하거나, "
                    + "관리 콘솔의 최초 설정에서 서버 데이터 폴더의 setup-token 으로 PIN 을 지정하세요.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!await _service.VerifyAsync(provided, ct))
            return Results.Problem(statusCode: 401, detail: "관리자 PIN 불일치");

        return await next(context);
    }
}

/// <summary>관리자 PIN 필터를 엔드포인트에 부착하는 확장.</summary>
public static class AdminPinExtensions
{
    public static TBuilder RequireAdminPin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, AdminPinFilter>();
}
