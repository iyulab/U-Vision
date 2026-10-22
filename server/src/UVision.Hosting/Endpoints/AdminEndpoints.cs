using UVision.Api.Auth;
using UVision.Api.Models;

namespace UVision.Api.Endpoints;

/// <summary>
/// 관리자 PIN 최초 설정(first-run bootstrap) — 계정 시스템 없는 셀프호스트에서 신규 설치가 서버 파일
/// 수정·재시작 없이 UI 만으로 PIN 을 정하게 한다.
///
/// <para><b>인증 모델</b>: 이 두 엔드포인트는 <see cref="AdminPinExtensions.RequireAdminPin"/> 을
/// <b>부착하지 않는다</b> — PIN 이 아직 없기 때문. <c>status</c> 는 무인증, <c>setup</c> 은 서버 데이터
/// 폴더의 일회용 setup-token(파일시스템 = 신뢰 루트)으로 게이트한다.</para>
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this RouteGroupBuilder parent)
    {
        var group = parent.MapGroup("/admin");

        // PIN 설정 상태(무인증) — 클라이언트가 최초 설정 화면 노출 여부를 결정.
        group.MapGet("/status", async (AdminPinService service, CancellationToken ct) =>
        {
            var status = await service.GetStatusAsync(ct);
            return Results.Ok(new AdminStatusResponse
            {
                PinConfigured = status.PinConfigured,
                SetupRequired = status.SetupRequired,
            });
        })
            .WithName("GetAdminStatus")
            .Produces<AdminStatusResponse>();

        // 최초 PIN 설정(토큰 게이트, RequireAdminPin 미부착).
        group.MapPost("/setup", async (
            AdminSetupInput input, AdminPinService service, CancellationToken ct) =>
        {
            var outcome = await service.CompleteSetupAsync(
                input.SetupToken ?? "", input.NewPin ?? "", ct);

            return outcome switch
            {
                AdminSetupOutcome.Ok => Results.NoContent(),
                AdminSetupOutcome.BootstrapDisabled => Results.Problem(
                    statusCode: 410,
                    detail: "명시적 관리자 PIN(UVision:AdminPin/ADMIN_PIN)이 설정되어 최초 설정이 비활성입니다."),
                AdminSetupOutcome.AlreadyConfigured => Results.Problem(
                    statusCode: 409,
                    detail: "관리자 PIN 이 이미 설정되었습니다. 재설정하려면 서버에서 admin-pin.json 을 삭제 후 재시작하세요."),
                AdminSetupOutcome.NoPendingSetup => Results.Problem(
                    statusCode: 409,
                    detail: "진행 중인 최초 설정이 없습니다(setup-token 부재). 서버를 재시작하면 토큰이 재생성됩니다."),
                AdminSetupOutcome.InvalidToken => Results.Problem(
                    statusCode: 401, detail: "setup-token 이 올바르지 않습니다."),
                AdminSetupOutcome.InvalidPin => Results.Problem(
                    statusCode: 400, detail: "새 PIN 은 공백 없이 4자리 이상이어야 합니다."),
                _ => Results.Problem(statusCode: 500, detail: "최초 설정 실패"),
            };
        })
            .WithName("CompleteAdminSetup")
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(409)
            .ProducesProblem(410)
            .ProducesProblem(500);
    }
}
