namespace UVision.Api.OpcUa;

/// <summary>
/// 최근 결과 창 — 개수 상한이 있는 메모리 목록. 놓친 구독자가 <c>GetResultListFiltered</c> 로 따라잡는 범위이고, 정본이 아니다
/// (정본은 엔진의 레코드 저장소). 가득 차면 가장 오래된 것부터 밀려난다. 같은 판정 id 는 한 번만 들어간다 — outbox 가 최소 한 번 보내므로
/// 같은 사건이 다시 와도 목록에 겹쳐 쌓이지 않는다. 스레드 안전.
/// </summary>
public sealed class OpcUaResultWindow(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<OpcUaResult> _order = new();
    private readonly Dictionary<string, OpcUaResult> _byId = new(StringComparer.Ordinal);

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int Count
    {
        get { lock (_gate) return _order.Count; }
    }

    /// <summary>창에 넣는다. 이미 있는 판정 id 면 넣지 않고 <c>false</c>.</summary>
    public bool TryAdd(OpcUaResult result)
    {
        lock (_gate)
        {
            if (_byId.ContainsKey(result.ResultId))
                return false;
            _order.Enqueue(result);
            _byId[result.ResultId] = result;
            while (_order.Count > Capacity)
                _byId.Remove(_order.Dequeue().ResultId);
            return true;
        }
    }

    /// <summary>창에서 뺀다(사건을 올리지 못했을 때 되돌리는 데 쓴다). 없으면 <c>false</c>.</summary>
    public bool TryRemove(string resultId)
    {
        lock (_gate)
        {
            if (!_byId.Remove(resultId))
                return false;
            // Queue 는 중간 삭제가 없다 — 되돌리기는 드문 경로라 다시 짓는다.
            var rest = _order.Where(r => r.ResultId != resultId).ToArray();
            _order.Clear();
            foreach (var r in rest)
                _order.Enqueue(r);
            return true;
        }
    }

    public OpcUaResult? Find(string resultId)
    {
        lock (_gate)
            return _byId.GetValueOrDefault(resultId);
    }

    /// <summary>도착 순서(오래된 것 먼저)로 필터에 맞는 것을 모아 한 쪽을 돌려준다.</summary>
    public ResultListPage List(ResultFilter filter, int startIndex, int maxResults)
    {
        OpcUaResult[] snapshot;
        lock (_gate)
            snapshot = _order.ToArray();
        var matches = snapshot.Where(filter.Matches).ToArray();
        var page = matches.Skip(startIndex);
        if (maxResults > 0)
            page = page.Take(maxResults);
        var items = page.ToArray();
        return new ResultListPage(items, IsComplete: startIndex + items.Length >= matches.Length);
    }
}

/// <summary>한 쪽 — <paramref name="IsComplete"/> 가 참이면 더 부를 필요가 없다.</summary>
public sealed record ResultListPage(IReadOnlyList<OpcUaResult> Items, bool IsComplete);

/// <summary>
/// <c>GetResultListFiltered</c> 의 필터. 비어 있는(또는 0) 조건은 걸지 않는다. 엔진이 모르는 값(측정 id · 부품 id · 외부 레시피 id ·
/// 외부 구성 id · 제품 id)은 결과에 없으므로 그 조건이 비어 있지 않으면 어떤 결과도 맞지 않는다.
/// </summary>
public sealed record ResultFilter(
    int ResultState = 0,
    string MeasId = "",
    string PartId = "",
    string ExternalRecipeId = "",
    string InternalRecipeId = "",
    string ExternalConfigurationId = "",
    string InternalConfigurationId = "",
    string ProductId = "",
    string JobId = "")
{
    public bool Matches(OpcUaResult r) =>
        (ResultState == 0 || ResultState == r.ResultState)
        && MeasId.Length == 0 && PartId.Length == 0 && ExternalRecipeId.Length == 0
        && ExternalConfigurationId.Length == 0 && ProductId.Length == 0
        && (InternalRecipeId.Length == 0 || InternalRecipeId == r.InternalRecipeId)
        && (InternalConfigurationId.Length == 0 || InternalConfigurationId == r.InternalConfigurationId)
        && (JobId.Length == 0 || JobId == r.JobId);
}
