using System.Security.Cryptography;
using System.Text;

namespace UVision.Benchmark;

/// <summary>측정 표본 한 장 — 데이터 루트 기준 상대 경로 · 분할(train/test/all) · 정답 클래스 · 내용 SHA-256.</summary>
internal sealed record Sample(string Path, string Split, string Class, string Sha256);

/// <summary>목록을 지으며 걸러 낸 것 — 같은 내용의 파일(분할 안 중복) · test 에 있으나 train 에도 있는 파일(분할 간 누수).</summary>
internal sealed record ManifestBuild(List<Sample> Samples, int DuplicatesDropped, int LeaksDropped);

/// <summary>
/// 측정 표본 목록 — 데이터 루트(<c>UVISION_DATA_ROOT</c>)의 원본을 건드리지 않고, 어느 파일이 어느 분할 · 클래스인지를
/// 루트의 <c>GEN/u-vision/manifests/{name}.tsv</c> 에 적는다. TSV 인 이유: 원본 폴더 이름에 쉼표가 들어 있다.
/// <para>
/// 내용 해시로 같은 파일을 한 장으로 줄인다 — 공개 데이터셋에는 같은 이미지가 이름만 바꿔 여러 번 들어 있기도 하고(분할 안 중복은
/// 측정을 몇 장의 반복으로 만든다), test 에 train 과 같은 파일이 있으면 최근접 거리 0 으로 «맞힌» 것처럼 보인다(누수). 둘 다 빼고 센다.
/// </para>
/// </summary>
internal static class SampleManifest
{
    private const string Header = "path\tsplit\tclass\tsha256";

    private static readonly HashSet<string> ImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

    public static string PathFor(string dataRoot, string name) =>
        System.IO.Path.Combine(dataRoot, "GEN", "u-vision", "manifests", name + ".tsv");

    /// <summary>
    /// <c>{dir}/{분할 폴더}/{클래스 폴더}/파일</c> 꼴을 읽는다. 폴더 이름은 사상표(<c>학습=train</c> · <c>정상=OK</c>)로 바꾸고,
    /// 사상표에 없는 폴더는 건너뛴다(조용히 다른 이름으로 읽지 않는다). 정렬은 경로 순 — 다시 지어도 같은 목록.
    /// </summary>
    public static ManifestBuild FromFolders(
        string dataRoot, string dir, IReadOnlyDictionary<string, string> splits, IReadOnlyDictionary<string, string> classes)
    {
        var samples = new List<Sample>();
        var root = System.IO.Path.Combine(dataRoot, dir);
        foreach (var (splitDir, split) in splits)
        foreach (var (classDir, cls) in classes)
        {
            var folder = System.IO.Path.Combine(root, splitDir, classDir);
            if (!Directory.Exists(folder)) continue;
            samples.AddRange(Directory.GetFiles(folder)
                .Where(f => ImageExts.Contains(System.IO.Path.GetExtension(f)))
                .Select(f => new Sample(Relative(dataRoot, f), split, cls, Hash(f))));
        }
        return Dedupe(samples);
    }

    /// <summary>
    /// 데이터셋이 함께 주는 분할 CSV(머리줄에 <c>split</c> · <c>label</c> · <c>image</c> 열, 선택 <c>object</c>)를 읽는다 — 폴더가
    /// 분할을 나타내지 않는 데이터셋(VisA 의 <c>split_csv/2cls_highshot.csv</c> 등)용. 이미지 경로는 <paramref name="dir"/> 기준.
    /// 값은 사상표로 바꾸고 사상표에 없는 행은 건너뛴다. 그 뒤는 <see cref="FromFolders"/> 와 같다(중복 · 누수 제거).
    /// </summary>
    public static ManifestBuild FromSplitCsv(
        string dataRoot, string dir, string csv, string? objectName,
        IReadOnlyDictionary<string, string> splits, IReadOnlyDictionary<string, string> classes)
    {
        var root = System.IO.Path.Combine(dataRoot, dir);
        var rows = ParseSplitCsv(File.ReadAllLines(System.IO.Path.Combine(root, csv), Encoding.UTF8), objectName, splits, classes);
        return Dedupe(rows.Select(r =>
        {
            var file = System.IO.Path.Combine(root, r.Image);
            return new Sample(Relative(dataRoot, file), r.Split, r.Class, Hash(file));
        }));
    }

    /// <summary>분할 CSV 의 행 → (이미지 상대 경로, 분할, 클래스). 열은 머리줄 이름으로 찾는다 — 순서에 기대지 않는다.</summary>
    internal static List<(string Image, string Split, string Class)> ParseSplitCsv(
        IReadOnlyList<string> lines, string? objectName,
        IReadOnlyDictionary<string, string> splits, IReadOnlyDictionary<string, string> classes)
    {
        if (lines.Count == 0) throw new InvalidDataException("분할 CSV 가 비어 있다");
        var header = lines[0].Split(',').Select(h => h.Trim()).ToList();
        int Col(string name) => header.IndexOf(name) is var i and >= 0
            ? i : throw new InvalidDataException($"분할 CSV 에 '{name}' 열이 없다(머리줄: {lines[0]})");
        var (split, label, image) = (Col("split"), Col("label"), Col("image"));
        var obj = objectName is null ? -1 : Col("object");
        var rows = new List<(string, string, string)>();
        for (var n = 1; n < lines.Count; n++)
        {
            if (lines[n].Length == 0) continue;
            var cols = lines[n].Split(',');
            if (cols.Length != header.Count)
                throw new InvalidDataException($"분할 CSV {n + 1}행의 열이 {cols.Length}개(머리줄 {header.Count}개) — 쉼표가 든 경로는 읽지 않는다");
            if (obj >= 0 && cols[obj] != objectName) continue;
            if (splits.TryGetValue(cols[split], out var s) && classes.TryGetValue(cols[label], out var c))
                rows.Add((cols[image], s, c));
        }
        return rows;
    }

    /// <summary>
    /// 경로 순으로 첫 장만 남긴다(같은 분할 안에서 같은 내용). 그다음 train 에 있는 내용을 test 에서 뺀다. 같은 내용이 서로 다른
    /// 클래스로 붙어 있으면 라벨이 모순이라 멈춘다 — 어느 쪽이 맞는지 이 도구는 모른다.
    /// </summary>
    internal static ManifestBuild Dedupe(IEnumerable<Sample> samples)
    {
        var ordered = samples.OrderBy(s => s.Path, StringComparer.Ordinal).ToList();
        var conflict = ordered.GroupBy(s => s.Sha256).FirstOrDefault(g => g.Select(s => s.Class).Distinct().Count() > 1);
        if (conflict is not null)
            throw new InvalidDataException(
                $"같은 내용이 다른 클래스로 붙어 있다: {string.Join(", ", conflict.Select(s => $"{s.Path}({s.Class})"))}");

        var unique = ordered.DistinctBy(s => (s.Split, s.Sha256)).ToList();
        var trainHashes = unique.Where(s => s.Split == "train").Select(s => s.Sha256).ToHashSet();
        var kept = unique.Where(s => s.Split != "test" || !trainHashes.Contains(s.Sha256)).ToList();
        return new ManifestBuild(kept, ordered.Count - unique.Count, unique.Count - kept.Count);
    }

    public static void Write(string path, IReadOnlyList<Sample> samples)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var s in samples)
            sb.Append(s.Path).Append('\t').Append(s.Split).Append('\t').Append(s.Class).Append('\t').Append(s.Sha256).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static List<Sample> Read(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0 || lines[0] != Header)
            throw new InvalidDataException($"표본 목록 머리줄이 다르다(다시 지을 것): {path}");
        var samples = new List<Sample>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var cols = lines[i].Split('\t');
            samples.Add(cols.Length == 4
                ? new Sample(cols[0], cols[1], cols[2], cols[3])
                : throw new InvalidDataException($"{path}:{i + 1} 열이 넷이 아니다"));
        }
        return samples;
    }

    /// <summary>
    /// 측정할 표본을 고른다 — 분할로 거르고(<c>null</c> 이면 전체), 클래스마다 최대 <paramref name="perClass"/> 장을 목록 전체에
    /// 고르게 띄워 뽑는다. 앞에서부터 자르면 파일 번호 순(대개 촬영 순)의 한 구간만 재게 된다. 결정론적 — 다시 돌려도 같은 표본.
    /// <paramref name="offset"/> 은 뽑는 자리를 목록 안에서 그만큼 밀어(끝을 넘으면 처음으로) 같은 간격의 다른 표본을 얻는다 —
    /// few-shot 기준 이미지를 고르는 데 결과가 얼마나 민감한지 재는 데 쓴다.
    /// </summary>
    public static List<Sample> Pick(IReadOnlyList<Sample> samples, string? split, int perClass, int offset = 0)
    {
        var picked = new List<Sample>();
        foreach (var group in samples.Where(s => split is null || s.Split == split).GroupBy(s => s.Class))
        {
            var all = group.ToList();
            var n = Math.Min(perClass, all.Count);
            for (var i = 0; i < n; i++)
                picked.Add(all[(int)(((long)i * all.Count / n + offset) % all.Count)]);
        }
        return picked;
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>루트 상대 경로(구분자 <c>/</c>) — 목록이 머신 · 드라이브 문자를 담지 않게.</summary>
    private static string Relative(string dataRoot, string file) =>
        System.IO.Path.GetRelativePath(dataRoot, file).Replace('\\', '/');
}
