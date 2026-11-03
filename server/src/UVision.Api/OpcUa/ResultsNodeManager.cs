using System.Globalization;
using System.Text;
using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Server;
using StatusCodes = Opc.Ua.StatusCodes;

namespace UVision.Api.OpcUa;

/// <summary>
/// 결과 노드 이름공간 — NodeId 는 문자열 식별자라 클라이언트가 고정해 쓸 수 있다
/// (<c>ns=&lt;이 URI 의 인덱스&gt;;s=ResultManagement</c> 등). 이름공간 인덱스는 서버가 정하므로 세션에서 URI 로 찾는다.
/// </summary>
public static class OpcUaResultNames
{
    /// <summary>이 서버가 소유하는 결과 노드의 이름공간 URI — 호스트 이름과 무관한 고정 값.</summary>
    public const string NamespaceUri = "urn:u-vision:result-management";

    public const string ResultManagement = "ResultManagement";
    public const string GetResultById = "GetResultById";
    public const string GetResultComponentsById = "GetResultComponentsById";
    public const string GetResultListFiltered = "GetResultListFiltered";
    public const string EventType = "UVisionResultReadyEventType";

    /// <summary>메서드 <c>error</c> 출력 — 표준은 0 = 성공, 양수 = 표준 예약, 음수 = 응용 정의라고만 정한다.</summary>
    public const int ErrorNone = 0;
    /// <summary>응용 정의: 그 판정 id 의 결과가 최근 결과 창에 없다.</summary>
    public const int ErrorNotFound = -1;
    /// <summary>응용 정의: 인자가 맞지 않는다(startIndex 가 maxResults 의 배수가 아님 등).</summary>
    public const int ErrorInvalidArgument = -2;
}

/// <summary>
/// 결과 관리 노드 관리자 — <c>ResultManagement</c> 객체 하나(메서드 셋 + 사건 알림자)와 사건 유형 노드를 이 서버에 올린다.
/// 읽기 전용이다: 클라이언트가 엔진에 쓰거나 판정에 개입하는 길은 없다.
/// </summary>
public sealed class ResultsNodeManager : AsyncCustomNodeManager
{
    private static readonly JsonWriterOptions Compact = new() { Indented = false };

    private readonly OpcUaResultHub _hub;
    private BaseObjectState? _management;
    private uint _handle;

    public ResultsNodeManager(IServerInternal server, ApplicationConfiguration configuration, OpcUaResultHub hub)
        : base(server, configuration, OpcUaResultNames.NamespaceUri)
    {
        _hub = hub;
    }

    private NodeId Id(string name) => new(name, NamespaceIndex);

    /// <summary>사건 유형의 NodeId.</summary>
    public NodeId EventTypeId => Id(OpcUaResultNames.EventType);

    public override async ValueTask CreateAddressSpaceAsync(
        IDictionary<NodeId, IList<IReference>> externalReferences, CancellationToken cancellationToken = default)
    {
        if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var objectsRefs))
            externalReferences[ObjectIds.ObjectsFolder] = objectsRefs = [];
        if (!externalReferences.TryGetValue(ObjectTypeIds.BaseEventType, out var eventTypeRefs))
            externalReferences[ObjectTypeIds.BaseEventType] = eventTypeRefs = [];

        // 사건 유형 — 서버의 유형 트리에 BaseEventType 의 하위로 올라가야 클라이언트의 선택 절이 우리 속성을 찾는다.
        var eventType = new BaseObjectTypeState
        {
            NodeId = EventTypeId,
            BrowseName = new QualifiedName(OpcUaResultNames.EventType, NamespaceIndex),
            DisplayName = new LocalizedText(OpcUaResultNames.EventType),
            SuperTypeId = ObjectTypeIds.BaseEventType,
            IsAbstract = false,
        };
        foreach (var d in ResultReadyEventState.Declarations)
        {
            var declaration = new PropertyState(eventType)
            {
                NodeId = Id(OpcUaResultNames.EventType + "." + d.Name),
                SymbolicName = d.Name,
                BrowseName = new QualifiedName(d.Name, NamespaceIndex),
                DisplayName = new LocalizedText(d.Name),
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                TypeDefinitionId = VariableTypeIds.PropertyType,
                ModellingRuleId = ObjectIds.ModellingRule_Mandatory,
                DataType = d.DataType,
                ValueRank = ValueRanks.Scalar,
            };
            eventType.AddChild(declaration);
        }
        eventType.AddReference(ReferenceTypeIds.HasSubtype, true, ObjectTypeIds.BaseEventType);
        eventTypeRefs.Add(new NodeStateReference(ReferenceTypeIds.HasSubtype, false, eventType.NodeId));
        await AddPredefinedNodeAsync(SystemContext, eventType, cancellationToken).ConfigureAwait(false);
        AddTypesToTypeTree(eventType);

        // ResultManagement 객체 — Objects 폴더 아래, 사건 알림자(서버 객체에서도 구독된다).
        var management = new BaseObjectState(null)
        {
            NodeId = Id(OpcUaResultNames.ResultManagement),
            BrowseName = new QualifiedName(OpcUaResultNames.ResultManagement, NamespaceIndex),
            DisplayName = new LocalizedText(OpcUaResultNames.ResultManagement),
            TypeDefinitionId = ObjectTypeIds.BaseObjectType,
            EventNotifier = EventNotifiers.SubscribeToEvents,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
        };
        management.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        objectsRefs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, management.NodeId));

        // 인자 이름 · 순서는 OPC 40100-1 §7.10.2 의 시그니처를 따른다. 구조 형식(ResultDataType · ResultIdDataType 등)
        // 대신 원시 형식을 쓰는 단순화는 CHANGELOG 에 적혀 있다.
        AddMethod(management, OpcUaResultNames.GetResultById, OnGetResultById,
            inputs:
            [
                Arg("resultId", DataTypeIds.String),
                Arg("timeout", DataTypeIds.Int32),
            ],
            outputs:
            [
                Arg("resultHandle", DataTypeIds.UInt32),
                Arg("result", DataTypeIds.String),
                Arg("error", DataTypeIds.Int32),
            ]);
        AddMethod(management, OpcUaResultNames.GetResultComponentsById, OnGetResultComponentsById,
            inputs:
            [
                Arg("resultId", DataTypeIds.String),
                Arg("timeout", DataTypeIds.Int32),
            ],
            outputs:
            [
                Arg("hasTransferableDataOnFile", DataTypeIds.Boolean),
                Arg("resultHandle", DataTypeIds.UInt32),
                Arg("isPartial", DataTypeIds.Boolean),
                Arg("isSimulated", DataTypeIds.Boolean),
                Arg("resultState", DataTypeIds.Int32),
                Arg("measId", DataTypeIds.String),
                Arg("partId", DataTypeIds.String),
                Arg("externalRecipeId", DataTypeIds.String),
                Arg("internalRecipeId", DataTypeIds.String),
                Arg("productId", DataTypeIds.String),
                Arg("externalConfigurationId", DataTypeIds.String),
                Arg("internalConfigurationId", DataTypeIds.String),
                Arg("jobId", DataTypeIds.String),
                Arg("creationTime", DataTypeIds.UtcTime),
                // 표준의 ProcessingTimesDataType(구조) 자리 — 이 엔진은 처리 시간을 싣지 않아 언제나 빈 값이다.
                Arg("processingTimes", DataTypeIds.BaseDataType),
                Arg("resultContent", DataTypeIds.String, ValueRanks.OneDimension),
                Arg("error", DataTypeIds.Int32),
            ]);
        AddMethod(management, OpcUaResultNames.GetResultListFiltered, OnGetResultListFiltered,
            inputs:
            [
                Arg("resultState", DataTypeIds.Int32),
                Arg("measId", DataTypeIds.String),
                Arg("partId", DataTypeIds.String),
                Arg("externalRecipeId", DataTypeIds.String),
                Arg("internalRecipeId", DataTypeIds.String),
                Arg("externalConfigurationId", DataTypeIds.String),
                Arg("internalConfigurationId", DataTypeIds.String),
                Arg("productId", DataTypeIds.String),
                Arg("jobId", DataTypeIds.String),
                Arg("maxResults", DataTypeIds.UInt32),
                Arg("startIndex", DataTypeIds.UInt32),
                Arg("timeout", DataTypeIds.Int32),
            ],
            outputs:
            [
                Arg("isComplete", DataTypeIds.Boolean),
                Arg("resultCount", DataTypeIds.UInt32),
                Arg("resultHandle", DataTypeIds.UInt32),
                Arg("resultList", DataTypeIds.String, ValueRanks.OneDimension),
                Arg("error", DataTypeIds.Int32),
            ]);

        await AddPredefinedNodeAsync(SystemContext, management, cancellationToken).ConfigureAwait(false);
        await AddRootNotifierAsync(management, cancellationToken).ConfigureAwait(false);
        _management = management;
        _hub.Attach(this);
    }

    public override async ValueTask DeleteAddressSpaceAsync(CancellationToken cancellationToken = default)
    {
        _hub.Detach(this);
        await base.DeleteAddressSpaceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>사건 하나를 올린다 — 구독자가 없어도 던지지 않는다(전달은 구독자 확인 없이 «서버가 받았다»까지).</summary>
    public void Raise(OpcUaResult result)
    {
        var management = _management ?? throw new InvalidOperationException("address space not created");
        var evt = new ResultReadyEventState(management, NamespaceIndex);
        evt.Initialize(SystemContext, management, EventSeverity.Low,
            new LocalizedText($"{result.Verdict} {result.InternalRecipeId} {result.ResultId}"));
        // Initialize 가 기반 유형(BaseEventType)으로 채우므로 그 뒤에 우리 유형으로 바꾼다 — 유형을 보는 선택 절 · OfType 이 이 값을 읽는다.
        evt.TypeDefinitionId = EventTypeId;
        evt.EventType!.Value = EventTypeId;
        evt.Fill(result);
        management.ReportEvent(SystemContext, evt);
    }

    // --- 메서드 ---------------------------------------------------------------------------------------------

    private ServiceResult OnGetResultById(ISystemContext context, NodeState method, ArrayOf<Variant> input, List<Variant> output)
    {
        if (!TryResultId(input, out var resultId))
            return StatusCodes.BadInvalidArgument;
        var found = _hub.Window.Find(resultId);
        output[0] = found is null ? 0u : NextHandle();
        output[1] = found is null ? "" : ResultJson(found);
        output[2] = found is null ? OpcUaResultNames.ErrorNotFound : OpcUaResultNames.ErrorNone;
        return ServiceResult.Good;
    }

    private ServiceResult OnGetResultComponentsById(ISystemContext context, NodeState method, ArrayOf<Variant> input, List<Variant> output)
    {
        if (!TryResultId(input, out var resultId))
            return StatusCodes.BadInvalidArgument;
        var r = _hub.Window.Find(resultId);
        output[0] = false;
        output[1] = r is null ? 0u : NextHandle();
        output[2] = r?.IsPartial ?? false;
        output[3] = false;
        output[4] = r?.ResultState ?? 0;
        output[5] = "";
        output[6] = "";
        output[7] = "";
        output[8] = r?.InternalRecipeId ?? "";
        output[9] = "";
        output[10] = "";
        output[11] = r?.InternalConfigurationId ?? "";
        output[12] = r?.JobId ?? "";
        output[13] = r is null ? default(DateTimeUtc) : DateTimeUtc.From(r.CreationTime);
        output[14] = default(Variant);
        output[15] = r is null ? new ArrayOf<string>(Array.Empty<string>()) : new ArrayOf<string>(new[] { ContentJson(r) });
        output[16] = r is null ? OpcUaResultNames.ErrorNotFound : OpcUaResultNames.ErrorNone;
        return ServiceResult.Good;
    }

    private ServiceResult OnGetResultListFiltered(ISystemContext context, NodeState method, ArrayOf<Variant> input, List<Variant> output)
    {
        if (input.Count < 12
            || !input[0].TryGetValue(out int state)
            || !Text(input[1], out var measId) || !Text(input[2], out var partId)
            || !Text(input[3], out var externalRecipe) || !Text(input[4], out var internalRecipe)
            || !Text(input[5], out var externalConfig) || !Text(input[6], out var internalConfig)
            || !Text(input[7], out var productId) || !Text(input[8], out var jobId)
            || !input[9].TryGetValue(out uint maxResults) || !input[10].TryGetValue(out uint startIndex))
            return StatusCodes.BadInvalidArgument;

        // 표준: startIndex 는 첫 호출에 0, 이어지는 호출에는 maxResults 의 배수.
        if (maxResults > int.MaxValue || startIndex > int.MaxValue || (maxResults > 0 && startIndex % maxResults != 0)
            || (maxResults == 0 && startIndex != 0))
        {
            output[0] = true;
            output[1] = 0u;
            output[2] = 0u;
            output[3] = new ArrayOf<string>(Array.Empty<string>());
            output[4] = OpcUaResultNames.ErrorInvalidArgument;
            return ServiceResult.Good;
        }

        var page = _hub.Window.List(
            new ResultFilter(state, measId, partId, externalRecipe, internalRecipe, externalConfig, internalConfig, productId, jobId),
            (int)startIndex, (int)maxResults);
        output[0] = page.IsComplete;
        output[1] = (uint)page.Items.Count;
        output[2] = NextHandle();
        output[3] = new ArrayOf<string>(page.Items.Select(ResultJson).ToArray());
        output[4] = OpcUaResultNames.ErrorNone;
        return ServiceResult.Good;
    }

    private uint NextHandle() => Interlocked.Increment(ref _handle);

    private static bool TryResultId(ArrayOf<Variant> input, out string resultId)
    {
        resultId = "";
        return input.Count >= 1 && Text(input[0], out resultId);
    }

    private static bool Text(Variant v, out string text)
    {
        if (v.IsNull)
        {
            text = "";
            return true;
        }
        return v.TryGetValue(out text!);
    }

    /// <summary>결과 내용(펴 놓은 사건 속성과 같은 값)의 JSON — <c>json</c> 은 봉투 <c>data</c> 원문을 문자열로 싣는다.</summary>
    public static string ContentJson(OpcUaResult r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Compact))
        {
            w.WriteStartObject();
            w.WriteString("verdict", r.Verdict);
            if (r.Class is not null) w.WriteString("class", r.Class);
            if (r.SettledByTier is not null) w.WriteString("settledBy", r.SettledByTier);
            if (r.SettledByKind is not null) w.WriteString("settledByKind", r.SettledByKind);
            w.WriteBoolean("requiresReview", r.RequiresReview);
            if (r.Count is { } n) w.WriteNumber("count", n);
            w.WriteString("json", r.Json);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>결과 한 건의 JSON — 표준 <c>ResultDataType</c> 의 필드 이름을 따르고, 구조 대신 원시 값으로 편다.</summary>
    public static string ResultJson(OpcUaResult r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Compact))
        {
            w.WriteStartObject();
            w.WriteString("resultId", r.ResultId);
            w.WriteBoolean("isPartial", r.IsPartial);
            w.WriteNumber("resultState", r.ResultState);
            w.WriteString("internalRecipeId", r.InternalRecipeId);
            w.WriteString("internalConfigurationId", r.InternalConfigurationId);
            w.WriteString("jobId", r.JobId);
            w.WriteString("creationTime", r.CreationTime.ToString("o", CultureInfo.InvariantCulture));
            w.WritePropertyName("resultContent");
            w.WriteStartArray();
            w.WriteRawValue(ContentJson(r));
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // --- 노드 만들기 도우미 -------------------------------------------------------------------------------------

    private static Argument Arg(string name, NodeId dataType, int valueRank = ValueRanks.Scalar) =>
        new() { Name = name, DataType = dataType, ValueRank = valueRank, Description = LocalizedText.From(name) };

    private void AddMethod(
        BaseObjectState parent, string name, GenericMethodCalledEventHandler handler,
        Argument[] inputs, Argument[] outputs)
    {
        var method = new MethodState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            NodeId = Id(OpcUaResultNames.ResultManagement + "." + name),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = new LocalizedText(name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            Executable = true,
            UserExecutable = true,
            OnCallMethod = handler,
        };
        method.InputArguments = ArgumentsProperty(method, "InputArguments", inputs);
        method.OutputArguments = ArgumentsProperty(method, "OutputArguments", outputs);
        parent.AddChild(method);
    }

    private PropertyState<ArrayOf<Argument>> ArgumentsProperty(MethodState method, string browseName, Argument[] arguments)
    {
        var property = new PropertyState<ArrayOf<Argument>>.Implementation<StructureBuilder<Argument>>(method)
        {
            NodeId = Id(method.SymbolicName + "." + browseName),
            BrowseName = QualifiedName.From(browseName == "InputArguments" ? BrowseNames.InputArguments : BrowseNames.OutputArguments),
            ReferenceTypeId = ReferenceTypeIds.HasProperty,
            TypeDefinitionId = VariableTypeIds.PropertyType,
            DataType = DataTypeIds.Argument,
            ValueRank = ValueRanks.OneDimension,
        };
        property.DisplayName = LocalizedText.From(property.BrowseName.Name!);
        property.Value = arguments;
        return property;
    }
}
