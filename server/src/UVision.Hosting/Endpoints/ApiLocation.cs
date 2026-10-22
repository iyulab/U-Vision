namespace UVision.Api.Endpoints;

/// <summary>
/// 201 <c>Location</c> 헤더 — 호스트가 정한 API 기본 경로(<see cref="UVisionOptions.ApiBasePath"/>)와
/// 앱의 PathBase 아래로 만든다. 경로를 문자열로 박으면 다른 기본 경로로 마운트한 호스트에서 틀린 URL 이 된다.
/// </summary>
public static class ApiLocation
{
    /// <param name="request">요청 — PathBase(리버스 프록시 · 서브 앱)를 얻는다.</param>
    /// <param name="options">API 기본 경로.</param>
    /// <param name="relative">기본 경로 아래 상대 경로(앞 <c>/</c> 없이). 구성 요소는 호출부가 이스케이프한다.</param>
    public static string For(HttpRequest request, UVisionOptions options, string relative) =>
        $"{request.PathBase}/{options.ApiBasePath.Trim('/')}/{relative}";
}
