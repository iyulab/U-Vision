namespace UVision.Api.Endpoints;

/// <summary>모든 U-Vision API를 단일 ApiBasePath 그룹 아래로 묶는다(공유 호스트 네임스페이스 격리).</summary>
public static class UVisionEndpoints
{
    public static void MapUVisionEndpoints(this IEndpointRouteBuilder app, string apiBasePath)
    {
        var group = app.MapGroup(apiBasePath);
        group.MapInspectEndpoints();
        group.MapScenarioEndpoints();
        group.MapReferenceEndpoints();
        group.MapLabelEndpoints();
    }
}
