using System.Runtime.InteropServices;

namespace UVision.Api.Storage;

/// <summary><c>{scenario}/memory/{encoderKey}/patches/{imageId}.f32</c> — float32 원시 배열(리틀 엔디언 장비 전제).</summary>
public sealed class FilePatchBankStore(StoragePaths paths) : IPatchBankStore
{
    public Task WriteAsync(
        string scenarioId, string encoderKey, string imageId, float[] patches, CancellationToken cancellationToken = default) =>
        StoragePaths.AtomicWriteAsync(
            paths.MemoryPatchFile(scenarioId, encoderKey, imageId), MemoryMarshal.AsBytes(patches.AsSpan()).ToArray(),
            cancellationToken);

    public async Task<float[]?> ReadAsync(
        string scenarioId, string encoderKey, string imageId, int? expectedLength = null,
        CancellationToken cancellationToken = default)
    {
        var path = paths.MemoryPatchFile(scenarioId, encoderKey, imageId);
        if (!File.Exists(path))
            return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0
            || (expectedLength is { } n && bytes.Length != n * sizeof(float)))
            return null;
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
