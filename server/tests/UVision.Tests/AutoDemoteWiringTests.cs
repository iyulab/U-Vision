using UVision.Api.Endpoints;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// A1 자동격하 트리거 게이트 단위 테스트.
/// 실 inspect 다회 호출은 mock ML 라벨 제어가 어렵다 → 트리거 헬퍼를 직접 호출해 발동 조건을 검증한다.
/// </summary>
public class AutoDemoteWiringTests
{
    // 트리거 게이트: 행 수가 CheckEvery 배수일 때만 true.
    [Theory]
    [InlineData(25, 25, true)]
    [InlineData(24, 25, false)]
    [InlineData(50, 25, true)]
    [InlineData(0, 25, false)] // 0건이면 미발동
    public void Trigger_FiresOnMultiple(int rowCount, int every, bool expected) =>
        Assert.Equal(expected, InspectEndpoints.ShouldCheckAutoDemote(rowCount, every));
}
