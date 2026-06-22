namespace UVision.Api.Services.Ml;

/// <summary>
/// ML 분류 시도의 결과 — <b>성공</b>(<see cref="Result"/>)과 <b>실패</b>(<see cref="FailureReason"/>)를
/// 구분한다. 비활성(provider none)은 애초에 분류를 호출하지 않으므로 이 타입으로 표현하지 않는다(null).
/// <para>
/// A3 degrade 가시성: 기존엔 실패와 비활성이 모두 <c>null</c> 로 뭉개져 ML 이 조용히 사라졌다.
/// 이 구분이 B3(후속)에서 degrade율을 시계열로 셀 수 있는 토대다.
/// </para>
/// <para>
/// 생성자는 private — <see cref="Success"/>/<see cref="Failure"/> 두 팩토리로만 만든다.
/// "성공인데 결과 null"·"실패인데 사유 null" 같은 무의미 상태를 <b>타입 레벨에서 구성 불가</b>하게 한다.
/// </para>
/// </summary>
public sealed record MlOutcome
{
    private MlOutcome() { }

    /// <summary>분류 성공 시 결과. 실패면 null.</summary>
    public MlClassification? Result { get; private init; }

    /// <summary>분류 실패 사유(예외 메시지). 성공이면 null.</summary>
    public string? FailureReason { get; private init; }

    /// <summary>분류가 실패했는가(enabled 인데 예외).</summary>
    public bool Failed => FailureReason is not null;

    /// <summary>분류 성공 결과를 감싼다.</summary>
    public static MlOutcome Success(MlClassification result) =>
        new() { Result = result };

    /// <summary>분류 실패(degrade)를 사유와 함께 감싼다.</summary>
    public static MlOutcome Failure(string reason) =>
        new() { FailureReason = reason };
}
