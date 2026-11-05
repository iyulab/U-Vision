using System.Text;

namespace UVision.Tests;

/// <summary>
/// 엔진이 열어 두고 줄을 덧붙이는 jsonl 을 시험이 읽을 때의 방식. <c>File.ReadAllLines</c> 는 읽기 전용 공유로 열어서, 쓰는 쪽이
/// 쓰기 핸들을 쥔 동안 Windows 에서 <see cref="IOException"/>(다른 프로세스가 사용 중) 으로 실패한다 — 결과를 기다리며 되풀이해 읽는
/// 시험이 가끔 깨지던 원인. 쓰기 공유를 허용해 열고, 마지막의 끝나지 않은 줄은 엔진 쪽 읽기와 같게 버린다.
/// </summary>
internal static class SharedFileReader
{
    public static string[] ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, new UTF8Encoding(false));
        var text = reader.ReadToEnd();
        var lines = text.Split('\n');
        // 마지막 요소는 개행 뒤의 빈 문자열이거나 쓰는 중인 불완전한 줄이다 — 둘 다 아직 «줄» 이 아니다.
        return lines.Take(lines.Length - 1).Select(l => l.TrimEnd('\r')).ToArray();
    }
}
