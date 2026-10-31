using UVision.Api.Configuration;
using UVision.Api.Services.Mloop;

namespace UVision.Api.Services.Counting;

/// <summary>
/// 설정의 Provider 값으로 검출기를 고른다(<see cref="Ml.MlClassifierFactory"/> 와 같은 꼴). 잘못된 값 · 빠진 필수 설정은
/// 기동에서 멈춘다.
/// </summary>
public static class ObjectDetectorFactory
{
    public static IObjectDetector Create(DetectionOptions options)
    {
        switch (options.Provider.ToLowerInvariant())
        {
            case "none":
                return new DisabledObjectDetector();

            case "mock":
                return new MockObjectDetector();

            case "mloop":
                if (string.IsNullOrWhiteSpace(options.Endpoint))
                    throw new ArgumentException("detection provider mloop 는 Endpoint(mloop serve base URL) 설정이 필요합니다");
                if (string.IsNullOrWhiteSpace(options.Model))
                    throw new ArgumentException("detection provider mloop 는 Model(검출 모델 이름) 설정이 필요합니다");
                if (options.TimeoutSeconds is < 1 or > 600)
                    throw new ArgumentException("UVision:Detection:TimeoutSeconds 는 1~600 이어야 합니다");
                return new MloopObjectDetector(
                    MloopClient.Create(options.Endpoint, options.TimeoutSeconds, options.Token), options);

            default:
                throw new ArgumentException($"알 수 없는 detection provider: {options.Provider}");
        }
    }
}
