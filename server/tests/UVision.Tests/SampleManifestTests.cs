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
    public void Pick_SpreadsEachClassAcrossTheWholeList_AndKeepsTheSplit()
    {
        List<Sample> samples =
        [
            .. Enumerable.Range(0, 10).Select(i => S($"ng/{i}", "test", "NG", $"n{i}")),
            .. Enumerable.Range(0, 4).Select(i => S($"ok/{i}", "test", "OK", $"o{i}")),
            S("ok/train", "train", "OK", "t0"),
        ];

        var picked = SampleManifest.Pick(samples, "test", perClass: 5);

        Assert.Equal(["ng/0", "ng/2", "ng/4", "ng/6", "ng/8", "ok/0", "ok/1", "ok/2", "ok/3"], picked.Select(s => s.Path));
        Assert.Equal(samples.Count, SampleManifest.Pick(samples, split: null, perClass: int.MaxValue).Count);
    }

    [Fact]
    public void SplitCsv_FindsColumnsByName_FiltersTheObject_AndSkipsUnmappedValues()
    {
        string[] lines =
        [
            "object,split,label,image,mask",
            "pcb1,train,normal,pcb1/Normal/1.JPG,",
            "pcb1,test,anomaly,pcb1/Anomaly/2.JPG,pcb1/Mask/2.png",
            "pcb2,train,normal,pcb2/Normal/3.JPG,",
            "pcb1,val,normal,pcb1/Normal/4.JPG,",
        ];
        var rows = SampleManifest.ParseSplitCsv(lines, "pcb1",
            new Dictionary<string, string> { ["train"] = "train", ["test"] = "test" },
            new Dictionary<string, string> { ["normal"] = "OK", ["anomaly"] = "NG" });

        Assert.Equal([("pcb1/Normal/1.JPG", "train", "OK"), ("pcb1/Anomaly/2.JPG", "test", "NG")], rows);
    }

    [Fact]
    public void SplitCsv_RowWithTheWrongColumnCount_Stops() =>
        Assert.Throws<InvalidDataException>(() => SampleManifest.ParseSplitCsv(
            ["split,label,image", "train,normal,a,b.JPG"], null,
            new Dictionary<string, string> { ["train"] = "train" }, new Dictionary<string, string> { ["normal"] = "OK" }));

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
