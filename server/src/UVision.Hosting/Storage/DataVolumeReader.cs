using UVision.Api.Configuration;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 데이터 루트가 놓인 볼륨의 여유 공간을 읽는다(<c>GET /health</c> 의 <c>data_volume</c>). 읽기 전용 · 부수효과 없음.
/// 읽을 수 없는 환경(UNC 경로 · 준비되지 않은 드라이브 · 권한)은 예외가 아니라 <c>null</c> — 상태 확인이 저장소 사정으로
/// 실패하면 안 된다. 판정 · 저장 경로는 이 값을 보지 않는다(엔진은 자동으로 아무것도 지우거나 멈추지 않는다 — 보존은 설치자 몫).
/// </summary>
public sealed class DataVolumeReader
{
    private readonly string _root;
    private readonly long _warnBytes;
    private readonly Func<string, (long Free, long Total)> _measure;

    public DataVolumeReader(StoragePaths paths, StorageOptions options, Func<string, (long Free, long Total)>? measure = null)
    {
        _root = paths.Root;
        _warnBytes = options.LowSpaceWarningBytes;
        _measure = measure ?? MeasureDrive;
    }

    public DataVolumeStatus? Read()
    {
        try
        {
            var (free, total) = _measure(_root);
            return Evaluate(free, total, _warnBytes);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>여유가 경고 문턱 미만이면 <c>low</c>. 문턱 0 이하는 경고 없음(신호만).</summary>
    public static DataVolumeStatus Evaluate(long freeBytes, long totalBytes, long warnBytes) => new()
    {
        FreeBytes = freeBytes,
        TotalBytes = totalBytes,
        Low = warnBytes > 0 && freeBytes < warnBytes,
    };

    private static (long Free, long Total) MeasureDrive(string path)
    {
        // 디렉터리가 든 볼륨 — 이 프로세스가 쓸 수 있는 여유(쿼터 반영)와 전체 크기.
        var drive = new DriveInfo(Path.GetPathRoot(path) is { Length: > 0 } root ? root : path);
        return (drive.AvailableFreeSpace, drive.TotalSize);
    }
}
