using UVision.Api.Configuration;
using UVision.Api.Services.Models;

namespace UVision.Api.Services.Ml;

/// <summary>
/// 설정의 Provider 값으로 구체 ML 분류기를 선택한다(<see cref="VlmProviderFactory"/> 와 동형).
/// 앱 코드는 <see cref="IMlClassifier"/> 만 안다.
/// </summary>
public static class MlClassifierFactory
{
    public static IMlClassifier Create(MlOptions options, ModelBindingResolver? resolver = null)
    {
        switch (options.Provider.ToLowerInvariant())
        {
            case "none":
                return new DisabledMlClassifier();

            case "mock":
                return new MockMlClassifier();

            case "mloop":
                if (string.IsNullOrWhiteSpace(options.Endpoint))
                    throw new ArgumentException("mloop provider 는 Endpoint(mloop serve base URL) 설정이 필요합니다");
                // 상대 경로(predict?name=) 해석을 위해 base URL 에 trailing slash 보장.
                var http = new HttpClient { BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/") };
                return new MloopClassifier(http, options, resolver);

            default:
                throw new ArgumentException($"알 수 없는 ML provider: {options.Provider}");
        }
    }
}
