using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace UVision.Client;

/// <summary>
/// <c>baseAddress</c> 에서 닿는 엔진의 <see cref="UVisionApiClient"/> 를 만든다. 엔진의 API 기준 경로는 경로마다가
/// 아니라 기준 URL 에 들어간다 — 엔진을 다른 기준 경로나 프록시 접두 뒤에 두어도 여기 인자만 바뀐다.
/// </summary>
public static class UVisionClient
{
    public const string DefaultApiBasePath = "/api/u-vision";

    /// <param name="httpClient">호출자가 소유한다(보통 <c>IHttpClientFactory</c>). 관리 호출은 <c>X-Admin-Pin</c> 헤더가
    /// 필요하다 — 이 클라이언트나 요청마다 호출자가 붙인다.</param>
    /// <param name="baseAddress">엔진에 닿는 주소(프록시 접두 포함), 예 <c>https://host/vision</c>.</param>
    /// <param name="apiBasePath">엔진에 설정된 API 기준 경로.</param>
    public static UVisionApiClient Create(HttpClient httpClient, Uri baseAddress, string apiBasePath = DefaultApiBasePath)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseAddress);
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient)
        {
            BaseUrl = BaseUrl(baseAddress, apiBasePath),
        };
        return new UVisionApiClient(adapter);
    }

    internal static string BaseUrl(Uri baseAddress, string apiBasePath)
    {
        var path = apiBasePath.Trim('/');
        return baseAddress.AbsoluteUri.TrimEnd('/') + (path.Length == 0 ? "" : "/" + path);
    }
}
