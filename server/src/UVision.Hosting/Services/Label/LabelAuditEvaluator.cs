namespace UVision.Api.Services.Label;

/// <summary>라벨 이벤트 모드 — 단일 출처(C1 provenance). 오라클은 D0 seam(미구현).</summary>
public static class LabelMode
{
    public const string Label = "label";   // 운영 라벨/해소(operative)
    public const string Audit = "audit";   // 블라인드 재라벨(측정 — operative 안 덮음)
    public const string Oracle = "oracle"; // ④-B 자리만, 코드 없음
}

/// <summary>감사 상태 — 단일 출처(C1).</summary>
public static class LabelAuditStatus
{
    public const string Unaudited = "unaudited";
    public const string Consistent = "consistent";
    public const string Conflicted = "conflicted";
    public const string Resolved = "resolved";
}

/// <summary>
/// 라벨 감사의 순수 로직(C1) — 안정 해시 표본 선정 + 감사 상태 평가. I/O·예외 없음
/// (<see cref="Services.DualCheck.DualCheckEvaluator"/> 와 동일 규율). 저장소 read-modify-write·블라인드
/// 강제는 store/endpoint 가 소유한다.
/// </summary>
public static class LabelAuditEvaluator
{
    /// <summary>FNV-1a 32-bit — 문화권·런타임 독립 안정 해시(<c>string.GetHashCode</c>는 런타임마다 달라 부적합).</summary>
    public static uint StableHash(string s)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }

    /// <summary>
    /// image_id 가 k% 표본에 드는가 — 결정론(RNG 없음). k=0 → 안 뽑음, k=100 → 항상.
    /// 같은 id 는 항상 같은 판정(재현 가능) + k 단조 상위집합.
    /// </summary>
    public static bool IsSampled(string imageId, int ratePercent) =>
        ratePercent > 0 && StableHash(imageId) % 100 < (uint)ratePercent;

    /// <summary>블라인드 재라벨 vs 직전 operative 라벨 → consistent/conflicted(ordinal — LabelSet 정합).</summary>
    public static string EvaluateAuditStatus(string priorOperativeLabel, string auditLabel) =>
        string.Equals(priorOperativeLabel, auditLabel, StringComparison.Ordinal)
            ? LabelAuditStatus.Consistent
            : LabelAuditStatus.Conflicted;
}
