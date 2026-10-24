extern alias bench;

using bench::UVision.Benchmark;
using Xunit;

namespace UVision.Tests;

/// <summary>측정 표본 목록의 중복 · 누수 제거 — 같은 내용은 한 장, train 에 있는 내용은 test 에서 뺀다.</summary>
public class SampleManifestTests
{
    private static Sample S(string path, string split, string cls, string sha) => new(path, split, cls, sha);

    [Fact]
    public void SameContentWithinASplit_CountsOnce_FirstPathWins()
    {
        var build = SampleManifest.Dedupe([S("t/ok/b.png", "train", "OK", "h1"), S("t/ok/a.png", "train", "OK", "h1"), S("t/ok/c.png", "train", "OK", "h2")]);

        Assert.Equal(["t/ok/a.png", "t/ok/c.png"], build.Samples.Select(s => s.Path));
        Assert.Equal((1, 0), (build.DuplicatesDropped, build.LeaksDropped));
    }

    [Fact]
    public void TestImageAlsoInTrain_IsDropped_AsALeak()
    {
        var build = SampleManifest.Dedupe([S("a", "train", "OK", "h1"), S("b", "test", "OK", "h1"), S("c", "test", "OK", "h2")]);

        Assert.Equal(["a", "c"], build.Samples.Select(s => s.Path));
        Assert.Equal((0, 1), (build.DuplicatesDropped, build.LeaksDropped));
    }

    [Fact]
    public void SameContentWithDifferentClasses_Stops()
    {
        Assert.Throws<InvalidDataException>(() => SampleManifest.Dedupe([S("a", "train", "OK", "h1"), S("b", "train", "NG", "h1")]));
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "uv-manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
        try
        {
            List<Sample> samples = [S("x/정상,1.png", "all", "OK", "h1"), S("x/불량/2.png", "all", "NG", "h2")];
            SampleManifest.Write(path, samples);
            Assert.Equal(samples, SampleManifest.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
