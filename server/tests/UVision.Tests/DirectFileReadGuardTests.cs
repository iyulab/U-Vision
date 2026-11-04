using System.Text.RegularExpressions;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 엔진 소스가 파일을 읽을 때 <c>File.OpenRead</c> · <c>File.ReadAll*</c> 를 직접 부르지 않고 <c>StoragePaths</c> 의 공유 읽기(<c>*Resilient*</c>)를 쓰게 지킨다.
/// 표준 읽기 호출은 Windows 에서 파일을 «쓰는 쪽을 배제하는 모드» 로 열어, 상태 파일을 교체하는 쪽 · 덧붙이는 쪽을 실패시키거나 읽는 쪽이 교체 순간에 던진다
/// (<see cref="StoragePathsResilienceTests"/> 가 그 증거 — 여기서는 새 직접 읽기가 다시 생기는 것을 막는다).
/// 예외: <c>UVision.Edge.Agent</c>(런타임 외 의존성 없음이 설계, 읽는 파일은 에이전트 자신이 쓰는 상태 파일) · 생성된 코드.
/// </summary>
public class DirectFileReadGuardTests
{
    private static readonly Regex Direct = new(@"\bFile\.(OpenRead|OpenText|ReadLines|ReadAll(Text|Bytes|Lines)(Async)?|Open)\(", RegexOptions.Compiled);

    private static string? FindSrc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "UVision.Hosting"))) return candidate;
        }
        return null;
    }

    [Fact]
    public void EngineSources_ReadFilesThroughTheSharedReadPath()
    {
        var src = FindSrc();
        Assert.NotNull(src);   // 소스 트리를 못 찾으면 가드가 아무것도 안 지키는 것이라 조용히 통과시키지 않는다

        var offenders = new[] { "UVision.Hosting", "UVision.Api" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(src!, project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(x => !x.Text.TrimStart().StartsWith("//") && !x.Text.TrimStart().StartsWith("///") && Direct.IsMatch(x.Text))
            .Select(x => $"{Path.GetRelativePath(src!, x.File)}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "직접 파일 읽기 — StoragePaths.OpenReadResilient[Async] · ReadAll{Bytes,Text,Lines}ResilientAsync 를 쓴다:\n" + string.Join('\n', offenders));
    }
}
