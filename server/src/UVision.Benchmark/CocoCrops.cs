using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace UVision.Benchmark;

/// <summary>
/// COCO 상자 주석 → 부품 크롭 표본 목록. 한 장에 부품이 여럿 찍힌 데이터셋(기판 사진에 부품마다 «정상_역삽» · «불량_역삽» 상자)을
/// 부품 한 개 = 표본 한 장으로 바꾼다 — 오투입 · 방향 확인은 부품 단위 질문이라. 원본은 건드리지 않고 크롭은 데이터 루트의
/// <c>GEN/u-vision/crops/&lt;이름&gt;/&lt;클래스&gt;/&lt;내용 해시&gt;.jpg</c> 에, 목록은 <c>GEN/u-vision/manifests/&lt;이름&gt;.tsv</c> 에 쓴다.
/// <para>
/// <c>--coco-dir</c> · <c>--image-dir</c> 는 <c>;</c> 로 여러 폴더(데이터 루트 기준). 주석 파일의 <c>images[].file_name</c> 을 이미지 폴더들에서
/// 찾는다. 상자 이름은 사상표(<c>--classes 정상_역삽=OK,불량_역삽=NG</c>)에 있는 것만 자른다. <c>--split</c> 은 이번 실행 크롭의 분할이고,
/// <c>--append</c> 면 기존 목록에 더해 중복 · 누수 제거를 다시 한다(Training → train, Validation → test 를 두 번 돌려 한 목록으로).
/// </para>
/// </summary>
internal static class CocoCrops
{
    public static int Run(Dictionary<string, string> o, Func<string, Dictionary<string, string>> pairs)
    {
        if (DataRoot.Resolve(o) is not { } root) return 1;
        if (!o.TryGetValue("name", out var name) || !o.TryGetValue("coco-dir", out var cocoDirs)
            || !o.TryGetValue("image-dir", out var imageDirs) || !o.TryGetValue("split", out var split)
            || !o.TryGetValue("classes", out var classSpec))
        {
            Console.Error.WriteLine("crops: --name · --coco-dir · --image-dir · --split(train|test) · --classes(상자 이름=클래스,…) 필수"
                + " — 선택 --pad 0.1 · --min-side 24 · --append");
            return 1;
        }
        var classes = pairs(classSpec);
        var pad = double.TryParse(o.GetValueOrDefault("pad"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0.1;
        var minSide = int.TryParse(o.GetValueOrDefault("min-side"), out var ms) ? ms : 24;

        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Split(imageDirs))
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
                images.TryAdd(Path.GetFileName(f), f);

        var outRoot = Path.Combine(root, "GEN", "u-vision", "crops", name);
        var samples = new List<Sample>();
        int files = 0, missing = 0, small = 0;
        foreach (var dir in Split(cocoDirs))
        foreach (var json in Directory.EnumerateFiles(Path.Combine(root, dir), "*.json", SearchOption.AllDirectories))
        {
            files++;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
            var cats = doc.RootElement.GetProperty("categories").EnumerateArray()
                .ToDictionary(c => c.GetProperty("id").GetInt32(), c => c.GetProperty("name").GetString() ?? "");
            var byImage = doc.RootElement.GetProperty("annotations").EnumerateArray()
                .Select(a => (Image: a.GetProperty("image_id").GetInt32(), Cat: cats.GetValueOrDefault(a.GetProperty("category_id").GetInt32(), ""),
                    Box: a.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble()).ToArray()))
                .Where(a => classes.ContainsKey(a.Cat))
                .GroupBy(a => a.Image)
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var img in doc.RootElement.GetProperty("images").EnumerateArray())
            {
                if (!byImage.TryGetValue(img.GetProperty("id").GetInt32(), out var boxes)) continue;
                if (!images.TryGetValue(img.GetProperty("file_name").GetString() ?? "", out var imagePath)) { missing++; continue; }
                using var bitmap = SKBitmap.Decode(imagePath);
                if (bitmap is null) { missing++; continue; }
                foreach (var b in boxes)
                {
                    if (CropRect(b.Box, bitmap.Width, bitmap.Height, pad, minSide) is not { } r) { small++; continue; }
                    using var crop = new SKBitmap();
                    if (!bitmap.ExtractSubset(crop, r)) { small++; continue; }
                    using var data = SKImage.FromBitmap(crop).Encode(SKEncodedImageFormat.Jpeg, 92);
                    var bytes = data.ToArray();
                    var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    var cls = classes[b.Cat];
                    var target = Path.Combine(outRoot, cls, sha[..16] + ".jpg");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
                    samples.Add(new Sample(Path.GetRelativePath(root, target).Replace('\\', '/'), split, cls, sha));
                }
            }
        }

        var path = SampleManifest.PathFor(root, name);
        var all = o.ContainsKey("append") && File.Exists(path) ? SampleManifest.Read(path).Concat(samples) : samples;
        var build = SampleManifest.Dedupe(all);
        SampleManifest.Write(path, build.Samples);
        foreach (var g in build.Samples.GroupBy(x => (x.Split, x.Class)).OrderBy(g => g.Key))
            Console.WriteLine($"{g.Key.Split}\t{g.Key.Class}\t{g.Count()}");
        Console.WriteLine($"→ {path} — 이번 실행 주석 {files}개 · 크롭 {samples.Count} · 이미지 못 찾음 {missing} · 작아서 뺌 {small}"
            + $" (목록 전체 {build.Samples.Count}장, 같은 내용 {build.DuplicatesDropped} · train 과 겹친 test {build.LeaksDropped} 뺌)");
        return samples.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// COCO 상자(<c>[x, y, w, h]</c>)를 변마다 <paramref name="pad"/> 비율만큼 넓혀 이미지 안으로 자른 사각형. 잘린 뒤 짧은 변이
    /// <paramref name="minSide"/> 보다 작으면 null — 몇 픽셀짜리 크롭은 판정 대상이 못 된다.
    /// </summary>
    internal static SKRectI? CropRect(IReadOnlyList<double> box, int width, int height, double pad, int minSide)
    {
        if (box.Count != 4) return null;
        var (x, y, w, h) = (box[0], box[1], box[2], box[3]);
        // 1e-9 안의 부동소수점 잔차(10 + 100 × 1.1 = 120.00000000000001)가 한 픽셀을 더하지 않게 반올림한 뒤 내림 · 올림한다.
        static double Snap(double v) => Math.Round(v, 9);
        var left = (int)Math.Floor(Snap(Math.Max(0, x - w * pad)));
        var top = (int)Math.Floor(Snap(Math.Max(0, y - h * pad)));
        var right = (int)Math.Ceiling(Snap(Math.Min(width, x + w * (1 + pad))));
        var bottom = (int)Math.Ceiling(Snap(Math.Min(height, y + h * (1 + pad))));
        return Math.Min(right - left, bottom - top) < minSide ? null : new SKRectI(left, top, right, bottom);
    }

    private static IEnumerable<string> Split(string dirs) =>
        dirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
