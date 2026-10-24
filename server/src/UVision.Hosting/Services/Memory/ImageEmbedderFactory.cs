using UVision.Api.Configuration;

namespace UVision.Api.Services.Memory;

/// <summary>
/// 설정의 Provider 값으로 임베더를 고른다(<see cref="Ml.MlClassifierFactory"/> 와 같은 꼴). 잘못된 값 · 빠진 필수
/// 설정은 기동에서 멈춘다.
/// </summary>
public static class ImageEmbedderFactory
{
    public static IImageEmbedder Create(MemoryOptions options)
    {
        if (options.Neighbors < 1)
            throw new ArgumentException("UVision:Memory:Neighbors 는 1 이상이어야 합니다");
        if (!(options.MaxDistance is > 0 and <= 2))
            throw new ArgumentException("UVision:Memory:MaxDistance 는 0 초과 2 이하(코사인 거리)여야 합니다");
        switch (options.Provider.ToLowerInvariant())
        {
            case "none":
                return new DisabledImageEmbedder();

            case "mock":
                return new MockImageEmbedder();

            case "mloop":
                if (string.IsNullOrWhiteSpace(options.Endpoint))
                    throw new ArgumentException("memory provider mloop 는 Endpoint(mloop serve base URL) 설정이 필요합니다");
                if (string.IsNullOrWhiteSpace(options.Encoder))
                    throw new ArgumentException("memory provider mloop 는 Encoder(mloop encoder list 의 id) 설정이 필요합니다");
                if (options.TimeoutSeconds is < 1 or > 600)
                    throw new ArgumentException("UVision:Memory:TimeoutSeconds 는 1~600 이어야 합니다");
                // 상대 경로(embed?encoder=) 해석을 위해 base URL 에 trailing slash 보장.
                var http = new HttpClient
                {
                    BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/"),
                    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                };
                return new MloopImageEmbedder(http, options);

            default:
                throw new ArgumentException($"알 수 없는 memory provider: {options.Provider}");
        }
    }
}
