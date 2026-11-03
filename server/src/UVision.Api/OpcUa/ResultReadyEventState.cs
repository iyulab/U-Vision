using Opc.Ua;

namespace UVision.Api.OpcUa;

/// <summary>
/// 우리 이름공간의 결과 준비 사건(<c>UVisionResultReadyEventType</c>, <c>BaseEventType</c> 의 하위 유형). OPC 40100-1 의
/// <c>ResultReadyEventType</c> 필수 속성(IsPartial · ResultState · InternalRecipeId · InternalConfigurationId · JobId · ResultId · CreationTime)과
/// 결과 내용을 펴 놓은 속성(Verdict · Class · SettledBy · SettledByKind · RequiresReview · Count · Json)을 같은 이름공간에서 갖는다.
/// 표준 이름공간의 <c>ResultReadyEventType</c> 이 아니므로, 그 유형의 NodeId 로 거는 필터에는 걸리지 않는다.
/// </summary>
public sealed class ResultReadyEventState : BaseEventState
{
    /// <summary>속성 선언 하나 — 유형 노드와 사건 인스턴스가 같은 표에서 지어져 둘이 어긋나지 않는다.</summary>
    public sealed record PropertyDeclaration(string Name, NodeId DataType);

    public static readonly IReadOnlyList<PropertyDeclaration> Declarations =
    [
        new("IsPartial", DataTypeIds.Boolean),
        new("ResultState", DataTypeIds.Int32),
        new("InternalRecipeId", DataTypeIds.String),
        new("InternalConfigurationId", DataTypeIds.String),
        new("JobId", DataTypeIds.String),
        new("ResultId", DataTypeIds.String),
        new("CreationTime", DataTypeIds.UtcTime),
        new("Verdict", DataTypeIds.String),
        new("Class", DataTypeIds.String),
        new("SettledBy", DataTypeIds.String),
        new("SettledByKind", DataTypeIds.String),
        new("RequiresReview", DataTypeIds.Boolean),
        new("Count", DataTypeIds.Int32),
        new("Json", DataTypeIds.String),
    ];

    private readonly Dictionary<string, BaseVariableState> _properties = new(StringComparer.Ordinal);

    public ResultReadyEventState(NodeState? parent, ushort namespaceIndex) : base(parent)
    {
        foreach (var d in Declarations)
        {
            var property = new PropertyState(this)
            {
                SymbolicName = d.Name,
                BrowseName = new QualifiedName(d.Name, namespaceIndex),
                DisplayName = new LocalizedText(d.Name),
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                TypeDefinitionId = VariableTypeIds.PropertyType,
                DataType = d.DataType,
                ValueRank = ValueRanks.Scalar,
            };
            AddChild(property);
            _properties[d.Name] = property;
        }
    }

    /// <summary>값을 채운다. <see cref="OpcUaResult.Count"/> 가 없으면 빈 값(null)으로 둔다.</summary>
    public void Fill(OpcUaResult r)
    {
        Set("IsPartial", r.IsPartial);
        Set("ResultState", r.ResultState);
        Set("InternalRecipeId", r.InternalRecipeId);
        Set("InternalConfigurationId", r.InternalConfigurationId);
        Set("JobId", r.JobId);
        Set("ResultId", r.ResultId);
        Set("CreationTime", DateTimeUtc.From(r.CreationTime));
        Set("Verdict", r.Verdict);
        Set("Class", r.Class ?? "");
        Set("SettledBy", r.SettledByTier ?? "");
        Set("SettledByKind", r.SettledByKind ?? "");
        Set("RequiresReview", r.RequiresReview);
        if (r.Count is { } n)
            Set("Count", n);
        Set("Json", r.Json);
    }

    private void Set(string name, Variant value) => _properties[name].Value = value;
}
