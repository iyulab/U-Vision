using UVision.Api.Configuration;
using UVision.Api.Services.Mloop;

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
        if (options.Patches && options.PatchBankCap is < 1 or > 5000)
            throw new ArgumentException("UVision:Memory:PatchBankCap 는 1~5000 이어야 합니다");
        var embedder = Build(options);
        if (options.Patches && !embedder.SupportsPatches)
            throw new ArgumentException("UVision:Memory:Patches 는 패치 모드를 지원하는 provider(mock · mloop)에서만 켤 수 있습니다");
        return embedder;
    }

    private static IImageEmbedder Build(MemoryOptions options)
    {
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
                return new MloopImageEmbedder(
                    MloopClient.Create(options.Endpoint, options.TimeoutSeconds, options.Token), options);

            default:
                throw new ArgumentException($"알 수 없는 memory provider: {options.Provider}");
        }
    }
}
