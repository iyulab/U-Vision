extern alias bench;

using bench::UVision.Benchmark;
using Xunit;

namespace UVision.Tests;

/// <summary>COCO 상자 → 크롭 사각형 — 여백을 넓히고 이미지 경계에서 자르며, 너무 작은 크롭은 뺀다.</summary>
public class CocoCropsTests
{
    [Fact]
    public void Box_IsPadded_AndClampedToTheImage()
    {
        var r = CocoCrops.CropRect([10, 20, 100, 50], width: 512, height: 512, pad: 0.1, minSide: 24)!.Value;
        Assert.Equal((0, 15, 120, 75), (r.Left, r.Top, r.Right, r.Bottom));

        var edge = CocoCrops.CropRect([480, 490, 40, 30], width: 512, height: 512, pad: 0.1, minSide: 10)!.Value;
        Assert.Equal((476, 487, 512, 512), (edge.Left, edge.Top, edge.Right, edge.Bottom));
    }

    [Fact]
    public void TooSmallAfterClamping_IsDropped() =>
        Assert.Null(CocoCrops.CropRect([500, 100, 20, 80], width: 512, height: 512, pad: 0, minSide: 24));
}
